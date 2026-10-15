using LighthouseManager.Tray.Integration;

namespace LighthouseManager.Tests.Tray;

/// <summary>
///     Tray autostart entry of the AppImage integration. Only the XDG autostart file is touched (temporary
///     XDG_CONFIG_HOME); the systemd part isn't exercised here because it would act on the real user session.
/// </summary>
// Changes process wide environment variables: must not run in parallel with other such tests
[Collection("Environment variables")]
public sealed class LinuxAutostartTests : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new()
    {
        ["XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        ["APPIMAGE"] = Environment.GetEnvironmentVariable("APPIMAGE")
    };

    private readonly string _root = Directory.CreateTempSubdirectory("lighthousemanager-tests").FullName;

    public LinuxAutostartTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
        Environment.SetEnvironmentVariable("APPIMAGE", "/opt/My Apps/LighthouseManager.AppImage");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, true);
    }

    private string AutostartFile => Path.Combine(_root, "autostart", "lighthousemanager-tray.desktop");

    [Fact]
    public void Disabling_keeps_the_entry_but_hides_it()
    {
        if (!OperatingSystem.IsLinux()) return;

        LinuxIntegration.SetTrayAutostart(true);
        Assert.True(LinuxIntegration.IsTrayAutostartEnabled());
        Assert.Contains("Exec=\"/opt/My Apps/LighthouseManager.AppImage\"", File.ReadAllText(AutostartFile));

        LinuxIntegration.SetTrayAutostart(false);
        Assert.False(LinuxIntegration.IsTrayAutostartEnabled());
        // The file stays (with Hidden=true), so the next tray start doesn't recreate an enabled entry
        Assert.Contains("Hidden=true", File.ReadAllText(AutostartFile));

        LinuxIntegration.SetTrayAutostart(true);
        Assert.True(LinuxIntegration.IsTrayAutostartEnabled());
    }

    [Fact]
    public void Missing_entry_counts_as_disabled()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.False(LinuxIntegration.IsTrayAutostartEnabled());
    }
}
