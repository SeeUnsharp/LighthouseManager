using System.Text.Json;
using System.Text.Json.Serialization;

namespace LighthouseManager.Tray;

/// <summary>
///     Settings of the tray app itself (the service's settings go through the API). Stored in <c>tray.json</c> in the
///     same per-user config directory as the service's settings file.
/// </summary>
/// <param name="BetaUpdates">Also offer pre-releases (e.g. 2.1.0-beta.1) as updates.</param>
public sealed record TraySettings(bool BetaUpdates = false)
{
    // Same location as the service's config directory (AppPaths in Core, which the tray doesn't reference)
    private static string ConfigDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LighthouseManager")
        : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "lighthousemanager");

    public static string FilePath => Path.Combine(ConfigDirectory, "tray.json");

    /// <summary>
    ///     The stored settings, or the defaults if there is no file or it can't be read.
    /// </summary>
    public static TraySettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), TraySettingsJsonContext.Default.TraySettings) ??
                  new TraySettings()
                : new TraySettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TraySettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, TraySettingsJsonContext.Default.TraySettings));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TraySettings))]
internal partial class TraySettingsJsonContext : JsonSerializerContext;
