using System.Diagnostics;

namespace LighthouseManager.Core;

/// <summary>
///     Names of the running processes, as <see cref="Process.ProcessName" /> reports them.
/// </summary>
/// <remarks>
///     On Linux <see cref="Process.GetProcesses()" /> reads several /proc files per process (~60 ms CPU for ~600
///     processes), too much for polling every second. Only /proc/&lt;pid&gt;/comm is read here (~5 ms). One difference:
///     for processes that overwrite their command line with a single string (Electron apps, avahi), .NET reports the
///     whole line ("codium --type=zygote"), this the name ("codium").
/// </remarks>
public static class RunningProcesses
{
    /// <summary>
    ///     Length of /proc/&lt;pid&gt;/comm when the name was truncated (TASK_COMM_LEN - 1).
    /// </summary>
    private const int TruncatedLength = 15;

    public readonly record struct Entry(int Id, string Name);

    public static IReadOnlyList<Entry> List()
    {
        return OperatingSystem.IsLinux() ? ListProc() : ListPortable();
    }

    /// <summary>
    ///     The full process name for a truncated comm, from argv[0] (or argv[1] for scripts), like .NET does.
    /// </summary>
    /// <param name="commandLine">/proc/&lt;pid&gt;/cmdline: arguments separated by '\0'.</param>
    internal static string ResolveName(string comm, string? commandLine)
    {
        if (comm.Length < TruncatedLength || string.IsNullOrEmpty(commandLine)) return comm;
        foreach (var arg in commandLine.Split('\0').Take(2))
        {
            var name = arg[(arg.LastIndexOf('/') + 1)..];
            if (name.StartsWith(comm, StringComparison.Ordinal)) return name;
        }

        return comm;
    }

    private static List<Entry> ListProc()
    {
        var processes = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n');
                var commandLine = comm.Length >= TruncatedLength
                    ? File.ReadAllText(Path.Combine(dir, "cmdline"))
                    : null;
                processes.Add(new Entry(pid, ResolveName(comm, commandLine)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Exited while enumerating
            }
        }

        return processes;
    }

    private static List<Entry> ListPortable()
    {
        var processes = new List<Entry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes.Add(new Entry(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating
                }
            }
        }

        return processes;
    }
}
