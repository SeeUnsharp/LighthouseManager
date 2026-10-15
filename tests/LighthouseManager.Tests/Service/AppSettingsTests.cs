using LighthouseManagerService;

namespace LighthouseManager.Tests.Service;

public class AppSettingsTests
{
    [Fact]
    public void Trigger_processes_default_to_SteamVR()
    {
        Assert.Equal(["vrserver"], new AppSettings().GetTriggerProcesses());
    }

    [Fact]
    public void Trigger_processes_are_trimmed_without_exe_and_deduplicated()
    {
        var settings = new AppSettings { TriggerProcesses = " vrserver.exe , monado-service,VRSERVER,, " };
        Assert.Equal(["vrserver", "monado-service"], settings.GetTriggerProcesses());
    }

    [Fact]
    public void Addresses_are_parsed_from_a_comma_separated_list()
    {
        var settings = new AppSettings { BaseStationAddresses = "AA:BB:CC:DD:EE:01, AA:BB:CC:DD:EE:02" };
        Assert.True(settings.TryGetAddresses(out var addresses));
        Assert.Equal([0xAABBCCDDEE01UL, 0xAABBCCDDEE02UL], addresses);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01,typo")]
    [InlineData("typo")]
    public void Invalid_addresses_are_rejected(string value)
    {
        Assert.False(new AppSettings { BaseStationAddresses = value }.TryGetAddresses(out _));
    }

    [Fact]
    public void Allowed_origins_are_split()
    {
        var settings = new AppSettings { AllowedOrigins = "http://localhost:5173, http://127.0.0.1:8080" };
        Assert.Equal(["http://localhost:5173", "http://127.0.0.1:8080"], settings.GetAllowedOrigins());
    }
}
