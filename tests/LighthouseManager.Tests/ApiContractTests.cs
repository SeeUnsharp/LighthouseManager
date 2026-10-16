using System.Text.Json;
using Client = LighthouseManager.Tray.Api;
using Server = LighthouseManagerService;

namespace LighthouseManager.Tests;

/// <summary>
///     The tray app keeps its own copies of the API DTOs (UIs are decoupled from the service). These tests make sure
///     the copies still match what the service sends.
/// </summary>
public class ApiContractTests
{
    private static readonly Server.StatusSnapshot Snapshot = new(true, true, "vrserver", ["vrserver", "monado-service"],
    [
        new Server.StationInfo("AA:BB:CC:DD:EE:01", "LHB-1A2B3C4D", LighthouseManager.Core.StationState.Awake, 0x0B,
            false, null, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)),
        new Server.StationInfo("AA:BB:CC:DD:EE:02", null, LighthouseManager.Core.StationState.Booting, null, true,
            "Not reachable")
    ], "/home/user/.config/lighthousemanager/appsettings.json", "2.1.0");

    [Fact]
    public void Status_snapshot_round_trips_from_service_to_tray()
    {
        var json = JsonSerializer.Serialize(Snapshot, Server.ApiJsonContext.Default.StatusSnapshot);
        var client = JsonSerializer.Deserialize(json, Client.ApiJsonContext.Default.StatusSnapshot)!;

        Assert.True(client.AutomaticMode);
        Assert.True(client.TriggerActive);
        Assert.Equal("vrserver", client.TriggerProcess);
        Assert.Equal(["vrserver", "monado-service"], client.TriggerProcesses);
        Assert.Equal("/home/user/.config/lighthousemanager/appsettings.json", client.SettingsFile);
        Assert.Equal("2.1.0", client.Version);
        Assert.Collection(client.Stations,
            s =>
            {
                Assert.Equal("AA:BB:CC:DD:EE:01", s.Address);
                Assert.Equal("LHB-1A2B3C4D", s.Name);
                Assert.Equal(Client.StationState.Awake, s.State);
                Assert.Equal((byte)0x0B, s.RawState);
                Assert.False(s.Busy);
                Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), s.UpdatedAt);
            },
            s =>
            {
                Assert.Null(s.Name);
                Assert.Equal(Client.StationState.Booting, s.State);
                Assert.True(s.Busy);
                Assert.Equal("Not reachable", s.Error);
            });
    }

    [Fact]
    public void Service_writes_documented_camelCase_names_and_enum_values()
    {
        // docs/API.md promises camelCase properties and camelCase enum strings
        var json = JsonSerializer.Serialize(Snapshot, Server.ApiJsonContext.Default.StatusSnapshot);

        Assert.Contains("\"automaticMode\":true", json);
        Assert.Contains("\"triggerProcesses\":[\"vrserver\",\"monado-service\"]", json);
        Assert.Contains("\"state\":\"awake\"", json);
        Assert.Contains("\"state\":\"booting\"", json);
        Assert.Contains("\"rawState\":11", json);
        Assert.Contains("\"settingsFile\":", json);
        Assert.Contains("\"version\":\"2.1.0\"", json);
    }

    [Fact]
    public void Every_service_station_state_is_known_to_the_tray()
    {
        foreach (var state in Enum.GetValues<LighthouseManager.Core.StationState>())
            Assert.True(Enum.TryParse<Client.StationState>(state.ToString(), out _), $"Tray misses state {state}");
    }

    [Fact]
    public void Discovery_result_and_add_request_round_trip()
    {
        var json = JsonSerializer.Serialize(new Server.DiscoveryResult(
        [
            new Server.DiscoveredStationInfo("AA:BB:CC:DD:EE:01", "LHB-1A2B3C4D", true),
            new Server.DiscoveredStationInfo("AA:BB:CC:DD:EE:02", "LHB-5E6F7A8B", false)
        ]), Server.ApiJsonContext.Default.DiscoveryResult);
        var client = JsonSerializer.Deserialize(json, Client.ApiJsonContext.Default.DiscoveryResult)!;

        Assert.Equal(["LHB-1A2B3C4D", "LHB-5E6F7A8B"], client.Stations.Select(s => s.Name));
        Assert.Equal([true, false], client.Stations.Select(s => s.Configured));
        Assert.Contains("\"configured\":false", json);

        var request = JsonSerializer.Serialize(new Client.AddStationsRequest(["AA:BB:CC:DD:EE:02"]),
            Client.ApiJsonContext.Default.AddStationsRequest);
        var server = JsonSerializer.Deserialize(request, Server.ApiJsonContext.Default.AddStationsRequest)!;
        Assert.Equal(["AA:BB:CC:DD:EE:02"], server.Addresses);
    }

    [Fact]
    public void Mqtt_config_round_trips_and_never_contains_a_password()
    {
        var json = JsonSerializer.Serialize(new Server.MqttConfig(true, "broker", 1883, "user", true, false,
            "homeassistant", "lighthousemanager", "", "gamingpc", true, null), Server.ApiJsonContext.Default.MqttConfig);
        var client = JsonSerializer.Deserialize(json, Client.ApiJsonContext.Default.MqttConfig)!;

        Assert.True(client.HasPassword);
        Assert.Equal("gamingpc", client.DefaultNodeId);
        Assert.True(client.Connected);
        Assert.DoesNotContain("\"password\"", json);

        var request = JsonSerializer.Serialize(new Client.MqttConfigRequest(true, "broker", 8883, "user", null, true,
            null, null, "pc"), Client.ApiJsonContext.Default.MqttConfigRequest);
        var server = JsonSerializer.Deserialize(request, Server.ApiJsonContext.Default.MqttConfigRequest)!;
        Assert.Null(server.Password);
        Assert.Equal(8883, server.Port);
        Assert.Equal("pc", server.NodeId);
    }

    [Fact]
    public void Command_result_round_trips()
    {
        var json = JsonSerializer.Serialize(new Server.CommandResult(false, Snapshot),
            Server.ApiJsonContext.Default.CommandResult);
        var client = JsonSerializer.Deserialize(json, Client.ApiJsonContext.Default.CommandResult)!;

        Assert.False(client.Success);
        Assert.Equal(2, client.Status.Stations.Length);
    }
}
