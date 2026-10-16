using LighthouseManager.Tray;

namespace LighthouseManager.Tests.Tray;

/// <summary>
///     The tray's own settings file (beta updates). Uses a temporary XDG_CONFIG_HOME, so it only runs on Linux.
/// </summary>
// Changes process wide environment variables: must not run in parallel with other such tests
[Collection("Environment variables")]
public sealed class TraySettingsTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string _root = Directory.CreateTempSubdirectory("lighthousemanager-tests").FullName;

    public TraySettingsTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_root, true);
    }

    // Fail instead of touching the real settings if XDG_CONFIG_HOME isn't ours (see UserSettingsTests)
    private void AssertIsolated() => Assert.StartsWith(_root, TraySettings.FilePath);

    [Fact]
    public void Beta_updates_are_off_without_a_file()
    {
        if (!OperatingSystem.IsLinux()) return;
        AssertIsolated();

        Assert.False(TraySettings.Load().BetaUpdates);
    }

    [Fact]
    public void Saved_setting_is_read_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        AssertIsolated();

        new TraySettings(BetaUpdates: true).Save();
        Assert.True(TraySettings.Load().BetaUpdates);
        Assert.Equal(Path.Combine(_root, "lighthousemanager", "tray.json"), TraySettings.FilePath);
    }

    [Fact]
    public void Broken_file_gives_the_defaults()
    {
        if (!OperatingSystem.IsLinux()) return;
        AssertIsolated();

        Directory.CreateDirectory(Path.GetDirectoryName(TraySettings.FilePath)!);
        File.WriteAllText(TraySettings.FilePath, "{ not json");
        Assert.False(TraySettings.Load().BetaUpdates);
    }
}
