using System.IO.Pipes;

namespace LighthouseManager.Tray;

/// <summary>
///     One tray per user: the first instance holds an exclusively opened lock file (released by the OS when the
///     process ends, also after a crash) and listens on a named pipe. A second start (Start menu, autostart, AppImage
///     double click) asks the running tray to show its flyout and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = $"LighthouseManager.Tray.{Environment.UserName}";

    private readonly CancellationTokenSource _cts = new();
    private readonly FileStream _lock;

    private SingleInstance(FileStream fileLock)
    {
        _lock = fileLock;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lock.Dispose();
    }

    /// <returns>The lock, or null if a tray is already running for this user.</returns>
    public static SingleInstance? TryAcquire()
    {
        var path = Path.Combine(StateDirectory, "tray.lock");
        Directory.CreateDirectory(StateDirectory);
        try
        {
            // FileShare.None is an exclusive lock on Windows and an flock() on Linux
            return new SingleInstance(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Asks the running tray to show its flyout.
    /// </summary>
    public static void ActivateRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(TimeSpan.FromSeconds(2));
            client.WriteByte(1);
        }
        catch (Exception)
        {
            // The running tray is still starting or shutting down; nothing else to do
        }
    }

    /// <summary>
    ///     Calls <paramref name="activate" /> whenever another start asks for the flyout.
    /// </summary>
    public void ListenForActivation(Action activate)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    if (server.ReadByte() >= 0) activate();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // Client went away; keep listening
                }
            }
        });
    }

    // Same location as the service's state directory (AppPaths in Core, which the tray doesn't reference)
    private static string StateDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LighthouseManager")
        : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state && Path.IsPathRooted(state)
                ? state
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
            "lighthousemanager");
}
