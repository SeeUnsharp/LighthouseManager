using System.Runtime.InteropServices;
using LighthouseManager.Core;

namespace LighthouseManagerService.Dimming;

/// <summary>
///     Reads the connected OpenXR applications from Monado (also used by Envision) through libmonado, Monado's client
///     library for tools like monado-ctl. Linux only.
/// </summary>
/// <remarks>
///     libmonado talks to the service over IPC, which must match the service's build. So the library is loaded from the
///     running service's installation (<c>bin/monado-service</c> → <c>lib/libmonado.so</c>), not from the library path:
///     Envision keeps one build per profile. Only this user's service processes count, and only a library no other
///     user can have placed (<see cref="TrustedFiles" />): it runs with this user's rights.
/// </remarks>
public sealed unsafe class MonadoSessions(ILogger logger) : IVrSessionSource
{
    private const int Success = 0;
    private const uint SupportedMajorVersion = 1;
    private static readonly string[] ServiceProcesses = ["monado-service", "wivrn-server"];

    private string? _libraryPath;
    private IntPtr _library;
    private IntPtr _root;
    private bool _unusable;
    private bool _missingReported;

    private delegate* unmanaged<uint*, uint*, uint*, void> _getVersion;
    private delegate* unmanaged<IntPtr*, int> _rootCreate;
    private delegate* unmanaged<IntPtr*, void> _rootDestroy;
    private delegate* unmanaged<IntPtr, int> _updateClientList;
    private delegate* unmanaged<IntPtr, uint*, int> _getNumberClients;
    private delegate* unmanaged<IntPtr, uint, uint*, int> _getClientIdAtIndex;
    private delegate* unmanaged<IntPtr, uint, IntPtr*, int> _getClientName;
    private delegate* unmanaged<IntPtr, uint, uint*, int> _getClientState;

    public IReadOnlyList<VrClient>? GetClients()
    {
        // While connected, the processes aren't searched: a stopped (or restarted) service fails the update below
        if (_root == IntPtr.Zero && !TryConnect()) return null;

        if (_updateClientList(_root) != Success)
        {
            // Service gone or restarted: connect again next time
            DestroyRoot();
            return null;
        }

        uint count;
        if (_getNumberClients(_root, &count) != Success) return null;
        var clients = new List<VrClient>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint id, flags;
            IntPtr name;
            if (_getClientIdAtIndex(_root, i, &id) != Success || _getClientState(_root, id, &flags) != Success)
                continue;
            var clientName = _getClientName(_root, id, &name) == Success
                ? Marshal.PtrToStringUTF8(name) ?? ""
                : "";
            clients.Add(new VrClient(clientName, (MonadoClientFlags)flags));
        }

        return clients;
    }

    public void Dispose()
    {
        Disconnect();
    }

    private bool TryConnect()
    {
        var path = FindLibrary(out var service, out var untrusted);
        if (path == null)
        {
            if (service != null && !_missingReported)
            {
                if (untrusted != null)
                    logger.LogWarning("Not loading {Library}: it or a folder above it can be changed by other users, " +
                                      "VR games are not detected", untrusted);
                else
                    logger.LogWarning("{Service} runs, but no libmonado was found next to it, VR games are not detected",
                        service);
            }

            _missingReported = service != null;
            Disconnect();
            return false;
        }

        if (path != _libraryPath)
        {
            Disconnect();
            _libraryPath = path;
            _unusable = !TryLoad(path);
        }

        if (_unusable) return false;

        IntPtr root;
        // Fails while the service is still starting (no IPC socket yet): just try again next time
        if (_rootCreate(&root) != Success) return false;
        _root = root;
        logger.LogInformation("Connected to the VR runtime through {Library}", path);
        return true;
    }

    /// <param name="service">Name of the running Monado service, also when its libmonado wasn't found.</param>
    /// <param name="untrusted">A libmonado that was found but not loaded, see <see cref="TrustedFiles" />.</param>
    /// <returns>libmonado of the running Monado service, or null.</returns>
    private static string? FindLibrary(out string? service, out string? untrusted)
    {
        service = untrusted = null;
        foreach (var process in RunningProcesses.List().Where(p => ServiceProcesses.Contains(p.Name)))
        {
            try
            {
                // Another user's process could name itself monado-service and point its command line to a library
                // of theirs, which would then run as this user
                if (!TrustedFiles.IsOwnProcess(process.Id)) continue;
                service = process.Name;

                var exe = FindExecutable(process.Id);
                var prefix = exe == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(exe));
                if (prefix == null) continue;

                foreach (var dir in new[] { "lib", "lib64", Path.Combine("lib", "x86_64-linux-gnu") })
                {
                    var libDir = Path.Combine(prefix, dir);
                    if (!Directory.Exists(libDir)) continue;
                    // Distribution packages may only ship the versioned file (libmonado.so.25)
                    var library = Directory.GetFiles(libDir, "libmonado.so*")
                        .OrderBy(f => f.Length)
                        .FirstOrDefault();
                    if (library == null) continue;
                    if (TrustedFiles.IsTrustedPath(library)) return library;
                    untrusted = library;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Exited meanwhile
            }
        }

        return null;
    }

    private static string? FindExecutable(int pid)
    {
        try
        {
            return File.ResolveLinkTarget($"/proc/{pid}/exe", false)?.FullName;
        }
        catch (UnauthorizedAccessException)
        {
            // Envision gives monado-service CAP_SYS_NICE, which makes /proc/<pid>/exe unreadable even for the same
            // user. The command line stays readable; Envision starts it with the full path.
        }

        var argv0 = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')[0];
        if (Path.IsPathRooted(argv0)) return argv0;
        // Started through PATH
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, argv0))
            .FirstOrDefault(File.Exists);
    }

    private bool TryLoad(string path)
    {
        if (!NativeLibrary.TryLoad(path, out _library))
        {
            logger.LogWarning("Couldn't load {Library}, VR games are not detected", path);
            return false;
        }

        try
        {
            _getVersion = (delegate* unmanaged<uint*, uint*, uint*, void>)Export("mnd_api_get_version");
            _rootCreate = (delegate* unmanaged<IntPtr*, int>)Export("mnd_root_create");
            _rootDestroy = (delegate* unmanaged<IntPtr*, void>)Export("mnd_root_destroy");
            _updateClientList = (delegate* unmanaged<IntPtr, int>)Export("mnd_root_update_client_list");
            _getNumberClients = (delegate* unmanaged<IntPtr, uint*, int>)Export("mnd_root_get_number_clients");
            _getClientIdAtIndex = (delegate* unmanaged<IntPtr, uint, uint*, int>)Export("mnd_root_get_client_id_at_index");
            _getClientName = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Export("mnd_root_get_client_name");
            _getClientState = (delegate* unmanaged<IntPtr, uint, uint*, int>)Export("mnd_root_get_client_state");
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogWarning("{Library} is not supported ({Message}), VR games are not detected", path, ex.Message);
            return false;
        }

        uint major, minor, patch;
        _getVersion(&major, &minor, &patch);
        if (major != SupportedMajorVersion)
        {
            logger.LogWarning("{Library} has API version {Major}.{Minor}.{Patch}, only {Supported}.x is supported, " +
                              "VR games are not detected", path, major, minor, patch, SupportedMajorVersion);
            return false;
        }

        return true;
    }

    private IntPtr Export(string name)
    {
        return NativeLibrary.GetExport(_library, name);
    }

    private void DestroyRoot()
    {
        if (_root == IntPtr.Zero) return;
        var root = _root;
        _rootDestroy(&root);
        _root = IntPtr.Zero;
        logger.LogInformation("Disconnected from the VR runtime");
    }

    public void Disconnect()
    {
        DestroyRoot();
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
        _libraryPath = null;
        _unusable = false;
    }
}
