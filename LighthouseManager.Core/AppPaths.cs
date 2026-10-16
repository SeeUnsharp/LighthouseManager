using System.Reflection;

namespace LighthouseManager.Core;

/// <summary>
///     Per-user locations for settings and logs. The installation folder isn't writable for installed builds (an
///     AppImage is read-only, Velopack replaces the app folder on updates), so nothing is written there.
/// </summary>
public static class AppPaths
{
    /// <summary>
    ///     Windows: %APPDATA%\LighthouseManager, Linux: $XDG_CONFIG_HOME/lighthousemanager (~/.config/...).
    /// </summary>
    public static string ConfigDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LighthouseManager")
        : Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "lighthousemanager");

    /// <summary>
    ///     User settings, layered over the appsettings.json next to the executable.
    /// </summary>
    public static string UserSettingsFile => Path.Combine(ConfigDirectory, "appsettings.json");

    /// <summary>
    ///     Windows: %LOCALAPPDATA%\LighthouseManager, Linux: $XDG_STATE_HOME/lighthousemanager (~/.local/state/...).
    /// </summary>
    public static string StateDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LighthouseManager")
        : Path.Combine(XdgDirectory("XDG_STATE_HOME", Path.Combine(".local", "state")), "lighthousemanager");

    public static string LogDirectory => Path.Combine(StateDirectory, "logs");

    /// <summary>
    ///     Product version, e.g. "2.0.0" (from the release tag in CI).
    /// </summary>
    public static string Version
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            // Strip the "+<commit>" suffix the SDK appends
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    private static string XdgDirectory(string variable, string fallbackBelowHome)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value)
            ? value
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallbackBelowHome);
    }
}
