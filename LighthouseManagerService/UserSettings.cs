using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LighthouseManager.Core;
using Microsoft.Extensions.Configuration.Json;

namespace LighthouseManagerService;

/// <summary>
///     Per-user settings file (<see cref="AppPaths.UserSettingsFile" />), layered over the appsettings.json next to
///     the executable. Installed builds can't keep user changes in the app folder (read-only AppImage, replaced on
///     updates).
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class UserSettingsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

internal static class UserSettings
{
    // BaseStationAddresses is commented out: an empty value here would override addresses configured in the
    // appsettings.json next to the program. The tray app adds it when you add base stations.
    private const string Template = """
        {
          // Your settings, see README.md. They override the defaults in the appsettings.json next to the program.
          // Restart the service after changing this file.
          "AppSettings": {
            // Comma separated base station MAC addresses. The tray app finds and adds them for you, or use
            // "LighthouseManager -d" to find them.
            // "BaseStationAddresses": "AA:BB:CC:DD:EE:01,AA:BB:CC:DD:EE:02"
          }
        }

        """;

    /// <summary>
    ///     Writes the base station list to the user settings file, keeping comments and other settings.
    /// </summary>
    public static void SaveBaseStationAddresses(IEnumerable<ulong> addresses)
    {
        Save("BaseStationAddresses", string.Join(",", addresses.Select(MacAddress.Format)));
    }

    /// <summary>
    ///     Writes one AppSettings entry to the user settings file, keeping comments and other settings.
    /// </summary>
    /// <param name="value">string or bool</param>
    public static void Save(string key, object value)
    {
        var literal = value switch
        {
            bool b => b ? "true" : "false",
            string s => JsonSerializer.Serialize(s, UserSettingsJsonContext.Default.String),
            _ => throw new ArgumentException($"Unsupported setting type {value.GetType()}", nameof(value))
        };
        var name = Regex.Escape(key);
        var file = AppPaths.UserSettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = File.Exists(file) ? File.ReadAllText(file) : Template;

        // An existing (not commented out) entry: replace its value
        var existing = new Regex($"^(?<prefix>\\s*\"{name}\"\\s*:\\s*)(?:\"(?:[^\"\\\\]|\\\\.)*\"|true|false|-?\\d+)",
            RegexOptions.Multiline);
        // The commented out example of the template: becomes the entry (it's the last one of the section, so no
        // trailing comma is needed)
        var example = new Regex($"^(?<indent>[ \\t]*)//[ \\t]*\"{name}\"[^\\r\\n]*", RegexOptions.Multiline);
        var section = new Regex("\"AppSettings\"\\s*:\\s*\\{");

        if (existing.IsMatch(text))
        {
            text = existing.Replace(text, m => m.Groups["prefix"].Value + literal, 1);
        }
        else if (example.IsMatch(text))
        {
            text = example.Replace(text, m => $"{m.Groups["indent"].Value}\"{key}\": {literal}", 1);
        }
        else if (section.IsMatch(text))
        {
            // First entry of the section; .NET's JSON configuration accepts the trailing comma this may leave
            text = section.Replace(text, m => $"{m.Value}\n    \"{key}\": {literal},", 1);
        }
        else
        {
            // No AppSettings section: add it through the JSON model (drops comments, keeps every setting)
            var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];
            if (root["AppSettings"] is not JsonObject appSettings)
                root["AppSettings"] = appSettings = [];
            appSettings[key] = JsonNode.Parse(literal);
            text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        }

        // Write a temporary file first, so a crash can't leave a half written settings file
        var temp = file + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, true);
    }

    public static void AddTo(ConfigurationManager configuration)
    {
        var file = AppPaths.UserSettingsFile;
        var directory = Path.GetDirectoryName(file)!;
        try
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(file)) File.WriteAllText(file, Template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings next to the executable still work
        }

        // After appsettings(.Environment).json, before user secrets, environment variables and command line, so
        // those still win
        var sources = configuration.Sources;
        var index = sources.Select((s, i) => (s, i))
            .LastOrDefault(x => x.s is JsonConfigurationSource { Path: { } path } &&
                                path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase), (null!, -1)).i;
        var source = new JsonConfigurationSource { Path = file, Optional = true, ReloadOnChange = false };
        // Splits the absolute path into a provider for the nearest existing directory and a relative path
        source.ResolveFileProvider();
        sources.Insert(index + 1, source);
    }
}
