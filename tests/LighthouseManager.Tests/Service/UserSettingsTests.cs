using LighthouseManager.Core;
using LighthouseManagerService;
using Microsoft.Extensions.Configuration;

namespace LighthouseManager.Tests.Service;

/// <summary>
///     Saving the base station list into the user settings file. Uses a temporary XDG_CONFIG_HOME, so it only runs
///     on Linux (where CI runs the tests).
/// </summary>
// Changes process wide environment variables: must not run in parallel with other such tests
[Collection("Environment variables")]
public sealed class UserSettingsTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string _root = Directory.CreateTempSubdirectory("lighthousemanager-tests").FullName;

    public UserSettingsTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_root, true);
    }

    private static string File => AppPaths.UserSettingsFile;

    private static IReadOnlyList<ulong> ReadBack()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(File, optional: false)
            .Build().GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
        Assert.True(settings.TryGetAddresses(out var addresses));
        return addresses;
    }

    [Fact]
    public void Template_is_created_without_an_active_address_entry()
    {
        if (!OperatingSystem.IsLinux()) return;

        UserSettings.AddTo(new ConfigurationManager());

        // An empty value would override addresses in the appsettings.json next to the program
        Assert.Contains("// \"BaseStationAddresses\"", System.IO.File.ReadAllText(File));
        Assert.Empty(ReadBack());
    }

    [Fact]
    public void Saving_into_the_template_keeps_its_comments()
    {
        if (!OperatingSystem.IsLinux()) return;
        UserSettings.AddTo(new ConfigurationManager());
        var comments = System.IO.File.ReadAllLines(File).Count(l => l.TrimStart().StartsWith("//"));

        UserSettings.SaveBaseStationAddresses([0xAABBCCDDEE01UL, 0xAABBCCDDEE02UL]);

        Assert.Equal([0xAABBCCDDEE01UL, 0xAABBCCDDEE02UL], ReadBack());
        // The example line became the real entry, the explaining comments stay
        Assert.Equal(comments - 1, System.IO.File.ReadAllLines(File).Count(l => l.TrimStart().StartsWith("//")));
    }

    [Fact]
    public void Saving_replaces_an_existing_entry_and_keeps_other_settings()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, """
            {
              "AppSettings": {
                "BaseStationAddresses": "AA:BB:CC:DD:EE:01",
                "TriggerProcesses": "monado-service" // my runtime
              }
            }
            """);

        UserSettings.SaveBaseStationAddresses([0xAABBCCDDEE02UL]);

        Assert.Equal([0xAABBCCDDEE02UL], ReadBack());
        var text = System.IO.File.ReadAllText(File);
        Assert.Contains("\"TriggerProcesses\": \"monado-service\" // my runtime", text);
    }

    [Fact]
    public void Saving_adds_the_entry_to_a_file_without_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, """{ "AppSettings": { "Interval": 2000 } }""");

        UserSettings.SaveBaseStationAddresses([0xAABBCCDDEE01UL]);

        Assert.Equal([0xAABBCCDDEE01UL], ReadBack());
        Assert.Contains("\"Interval\": 2000", System.IO.File.ReadAllText(File));
    }
}
