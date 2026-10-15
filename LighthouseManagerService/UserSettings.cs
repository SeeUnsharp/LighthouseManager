using LighthouseManager.Core;
using Microsoft.Extensions.Configuration.Json;

namespace LighthouseManagerService;

/// <summary>
///     Per-user settings file (<see cref="AppPaths.UserSettingsFile" />), layered over the appsettings.json next to
///     the executable. Installed builds can't keep user changes in the app folder (read-only AppImage, replaced on
///     updates).
/// </summary>
internal static class UserSettings
{
    private const string Template = """
        {
          // Your settings, see README.md. They override the defaults in the appsettings.json next to the program.
          // Restart the service after changing this file.
          "AppSettings": {
            // Comma separated base station MAC addresses, find them with "LighthouseManager -d"
            "BaseStationAddresses": ""
          }
        }

        """;

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
