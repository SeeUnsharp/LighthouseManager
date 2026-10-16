using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LighthouseManager.Core;

namespace LighthouseManagerService.Mqtt;

/// <summary>
///     Connection to an MQTT broker for the Home Assistant integration. Stored in its own file (it contains the
///     password), readable by the user only, see <see cref="MqttSettingsStore" />.
/// </summary>
public sealed record MqttSettings
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 1883;

    public string Username { get; init; } = "";

    public string Password { get; init; } = "";

    public bool UseTls { get; init; }

    /// <summary>
    ///     Home Assistant's discovery prefix (default of the MQTT integration: "homeassistant").
    /// </summary>
    public string DiscoveryPrefix { get; init; } = "homeassistant";

    /// <summary>
    ///     Root of LighthouseManager's own topics.
    /// </summary>
    public string BaseTopic { get; init; } = "lighthousemanager";

    /// <summary>
    ///     Identifies this computer in topics and Home Assistant; empty = derived from the computer name.
    /// </summary>
    public string NodeId { get; init; } = "";

    /// <summary>
    ///     <see cref="NodeId" />, or the computer name made topic safe.
    /// </summary>
    [JsonIgnore]
    public string EffectiveNodeId => NodeId.Length > 0 ? NodeId : DefaultNodeId;

    public static string DefaultNodeId
    {
        get
        {
            var id = Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9_-]", "_").Trim('_');
            return id.Length > 0 ? id : "computer";
        }
    }

    /// <returns>Why the settings can't be used, or null if they are fine.</returns>
    public string? Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Host)) return "Enter the broker address.";
        if (Port is < 1 or > 65535) return "The port must be between 1 and 65535.";
        if (!IsTopicLevelPath(DiscoveryPrefix)) return "The discovery prefix must not be empty or contain +, # or spaces.";
        if (!IsTopicLevelPath(BaseTopic)) return "The base topic must not be empty or contain +, # or spaces.";
        if (NodeId.Length > 0 && !Regex.IsMatch(NodeId, "^[A-Za-z0-9_-]+$"))
            return "The node ID may only contain letters, digits, _ and -.";
        return null;
    }

    private static bool IsTopicLevelPath(string topic) =>
        topic.Length > 0 && !topic.StartsWith('/') && !topic.EndsWith('/') && !Regex.IsMatch(topic, "[+#\\s]");
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(MqttSettings))]
internal partial class MqttSettingsJsonContext : JsonSerializerContext;

/// <summary>
///     Reads and writes <c>mqtt.json</c> next to the user settings file. On Linux the file is created with 0600
///     permissions; on Windows the user profile is private to the user anyway.
/// </summary>
public static class MqttSettingsStore
{
    public static string FilePath => Path.Combine(AppPaths.ConfigDirectory, "mqtt.json");

    /// <summary>
    ///     The stored settings; defaults (off) if there is no file or it can't be read (logged).
    /// </summary>
    public static MqttSettings Load(ILogger logger)
    {
        try
        {
            if (!File.Exists(FilePath)) return new MqttSettings();
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), MqttSettingsJsonContext.Default.MqttSettings)
                   ?? new MqttSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("MQTT: can't read {File}, Home Assistant integration stays off: {Message}", FilePath,
                ex.Message);
            return new MqttSettings();
        }
    }

    public static void Save(MqttSettings settings)
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var temp = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, MqttSettingsJsonContext.Default.MqttSettings);

        // Create the temporary file with owner-only permissions before the password is written into it
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(json);

        File.Move(temp, FilePath, true);
    }
}
