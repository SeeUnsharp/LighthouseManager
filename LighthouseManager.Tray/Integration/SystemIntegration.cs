using System.Diagnostics;

namespace LighthouseManager.Tray.Integration;

/// <summary>
///     Sets up what the installer promises: the service starts at login, the tray starts at login, and a Start menu
///     entry exists. Only for installed builds (Velopack Setup.exe on Windows, AppImage on Linux); the zip
///     downloads stay manual (README).
/// </summary>
public static class SystemIntegration
{
    public const string ServiceExecutable = "LighthouseManagerService";
    public const string CliExecutable = "LighthouseManager";

    /// <summary>
    ///     Whether this build manages autostart itself, i.e. it runs installed rather than from a zip download.
    /// </summary>
    public static bool IsManaged => OperatingSystem.IsWindows() ? WindowsIntegration.IsInstalled
        : OperatingSystem.IsLinux() && LinuxIntegration.AppImagePath != null;

    /// <summary>
    ///     Windows: after Setup.exe installed the app (Velopack hook). Linux: on every start, so a moved or renamed
    ///     AppImage keeps working.
    /// </summary>
    public static void Install()
    {
        if (OperatingSystem.IsWindows()) WindowsIntegration.Install();
        else if (OperatingSystem.IsLinux()) LinuxIntegration.Install();
    }

    public static void Uninstall()
    {
        if (OperatingSystem.IsWindows()) WindowsIntegration.Uninstall();
        else if (OperatingSystem.IsLinux()) LinuxIntegration.Uninstall();
    }

    public static void StopService()
    {
        if (OperatingSystem.IsWindows()) WindowsIntegration.StopService();
        else if (OperatingSystem.IsLinux()) LinuxIntegration.Systemctl("stop", LinuxIntegration.UnitName);
    }

    public static void StartService()
    {
        if (OperatingSystem.IsWindows()) WindowsIntegration.StartService();
        else if (OperatingSystem.IsLinux()) LinuxIntegration.Systemctl("start", LinuxIntegration.UnitName);
    }

    public static void RestartService()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsIntegration.StopService();
            WindowsIntegration.StartService();
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxIntegration.Systemctl("restart", LinuxIntegration.UnitName);
        }
    }

    /// <summary>
    ///     Opens a file with its default application (e.g. the settings file in a text editor).
    /// </summary>
    public static void OpenFile(string path)
    {
        if (OperatingSystem.IsLinux())
            Process.Start(new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false })?.Dispose();
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>
    ///     An AppImage has a single entry point (this app), so the service and the CLI are reached through it:
    ///     "LighthouseManager.AppImage --service" and "... --cli -d". Runs the sibling executable and passes its exit
    ///     code through.
    /// </summary>
    /// <returns>The exit code, or null if <paramref name="args" /> isn't a forwarding request.</returns>
    public static int? TryForward(string[] args)
    {
        var target = args.FirstOrDefault() switch
        {
            "--service" => ServiceExecutable,
            "--cli" => CliExecutable,
            _ => null
        };
        if (target == null) return null;

        var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? target + ".exe" : target);
        using var process = Process.Start(new ProcessStartInfo(path, args.Skip(1)) { UseShellExecute = false });
        if (process == null) return 1;

        // systemd stops the unit with SIGTERM; pass it on so the service shuts down cleanly
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Signal(process, "TERM");
            });
        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, context =>
            {
                // The child gets Ctrl+C from the terminal itself
                context.Cancel = true;
            });

        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Signal(Process process, string signal)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            Process.Start(new ProcessStartInfo("kill", [$"-{signal}", process.Id.ToString()])
                { UseShellExecute = false })?.WaitForExit();
        }
        catch (Exception)
        {
            // Child already gone
        }
    }

    internal static int Run(string file, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
