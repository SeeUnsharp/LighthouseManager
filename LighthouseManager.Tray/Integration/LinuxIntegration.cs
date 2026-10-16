using System.Runtime.Versioning;

namespace LighthouseManager.Tray.Integration;

/// <summary>
///     Linux (AppImage): systemd user unit for the service, XDG autostart entry for the tray, application menu entry
///     and icon. All point to the AppImage file, so they are rewritten when it was moved.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxIntegration
{
    public const string UnitName = "lighthousemanager.service";
    private const string IconName = "lighthousemanager";

    /// <summary>
    ///     Path of the running AppImage (set by the AppImage runtime), or null when not running as AppImage.
    /// </summary>
    public static string? AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path
        ? path
        : null;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConfigHome => Xdg("XDG_CONFIG_HOME", ".config");

    private static string DataHome => Xdg("XDG_DATA_HOME", Path.Combine(".local", "share"));

    private static string UnitFile => Path.Combine(ConfigHome, "systemd", "user", UnitName);

    private static string AutostartFile => Path.Combine(ConfigHome, "autostart", "lighthousemanager-tray.desktop");

    private static string MenuFile => Path.Combine(DataHome, "applications", "lighthousemanager.desktop");

    private static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");

    public static void Install()
    {
        if (AppImagePath is not { } appImage) return;
        var exec = Quote(appImage);

        WriteIfChanged(IconFile, Icon());
        WriteIfChanged(MenuFile, DesktopEntry(exec, autostart: false, hidden: false));
        // Keep the user's choice (Hidden=true = autostart off); only the path is updated
        WriteIfChanged(AutostartFile, DesktopEntry(exec, autostart: true,
            hidden: File.Exists(AutostartFile) && !IsTrayAutostartEnabled()));

        // Type=exec, not notify: the AppImage entry point forwards to the service binary, and .NET only sends the
        // readiness notification when its parent process is systemd
        var unit = $"""
            [Unit]
            Description=LighthouseManager - wakes and sleeps Lighthouse base stations
            After=bluetooth.target

            [Service]
            Type=exec
            ExecStart={exec} --service
            Restart=on-failure

            [Install]
            WantedBy=default.target

            """;
        var firstInstall = !File.Exists(UnitFile);
        // E.g. after the old AppImage was deleted: systemd refuses to start a unit that hit its restart limit
        Systemctl("reset-failed", UnitName);
        if (WriteIfChanged(UnitFile, System.Text.Encoding.UTF8.GetBytes(unit)))
        {
            Systemctl("daemon-reload");
            // Autostart on by default; later the user's choice (settings) is kept
            if (firstInstall) Systemctl("enable", UnitName);
            // Restart picks up a changed AppImage path; start if it wasn't running
            Systemctl("restart", UnitName);
        }
        else
        {
            // The tray needs the service, also when it doesn't start at login
            Systemctl("start", UnitName);
        }
    }

    public static bool IsServiceAutostartEnabled() => Systemctl("is-enabled", "--quiet", UnitName) == 0;

    public static void SetServiceAutostart(bool enabled) => Systemctl(enabled ? "enable" : "disable", UnitName);

    public static bool IsTrayAutostartEnabled()
    {
        if (!File.Exists(AutostartFile)) return false;
        return !File.ReadAllLines(AutostartFile).Any(l => l.Trim().Equals("Hidden=true", StringComparison.OrdinalIgnoreCase));
    }

    public static void SetTrayAutostart(bool enabled)
    {
        if (AppImagePath is not { } appImage) return;
        WriteIfChanged(AutostartFile, DesktopEntry(Quote(appImage), autostart: true, hidden: !enabled));
    }

    public static void Uninstall()
    {
        Systemctl("disable", "--now", UnitName);
        Systemctl("reset-failed", UnitName);
        foreach (var file in new[] { UnitFile, AutostartFile, MenuFile, IconFile })
            if (File.Exists(file))
                File.Delete(file);
        Systemctl("daemon-reload");
    }

    public static int Systemctl(params string[] args) => SystemIntegration.Run("systemctl", ["--user", .. args]);

    /// <param name="hidden">Autostart entry only: Hidden=true disables it (XDG autostart spec).</param>
    private static byte[] DesktopEntry(string exec, bool autostart, bool hidden)
    {
        var text = $"""
            [Desktop Entry]
            Type=Application
            Name=LighthouseManager
            Comment=Control Lighthouse base stations
            Exec={exec}
            Icon={IconName}
            Terminal=false
            Categories=Utility;
            {(autostart ? $"X-GNOME-Autostart-enabled={(hidden ? "false" : "true")}\nHidden={(hidden ? "true" : "false")}\n" : "")}
            """;
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Icon()
    {
        using var stream = typeof(LinuxIntegration).Assembly.GetManifestResourceStream("icon.png")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <returns>Whether the file was written (missing or different content).</returns>
    private static bool WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return true;
    }

    // Desktop entries and systemd both accept double-quoted paths with backslash escapes
    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Xdg(string variable, string fallbackBelowHome)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : Path.Combine(Home, fallbackBelowHome);
    }
}
