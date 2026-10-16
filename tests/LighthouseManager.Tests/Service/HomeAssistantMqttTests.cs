using System.Text.Json.Nodes;
using LighthouseManager.Core;
using LighthouseManagerService;
using LighthouseManagerService.Mqtt;

namespace LighthouseManager.Tests.Service;

public class HomeAssistantMqttTests
{
    private static readonly MqttSettings Settings = new() { Enabled = true, Host = "broker", NodeId = "gamingpc" };
    private readonly HomeAssistantMqtt _ha = new(Settings, "2.1.0");

    private static readonly StationInfo Awake = new("AA:BB:CC:DD:EE:01", "LHB-1A2B3C4D", StationState.Awake, 0x0B);
    private static readonly StationInfo Unknown = new("AA:BB:CC:DD:EE:02");

    private static StatusSnapshot Status(params StationInfo[] stations) =>
        new(true, true, "vrserver", ["vrserver"], stations, "/settings.json", "2.1.0");

    [Fact]
    public void Topics_are_per_computer()
    {
        Assert.Equal("lighthousemanager/gamingpc", _ha.BaseTopic);
        Assert.Equal("lighthousemanager/gamingpc/availability", _ha.AvailabilityTopic);
        Assert.Equal("lighthousemanager/gamingpc/cmd/#", _ha.CommandFilter);
    }

    [Fact]
    public void Discovery_announces_computer_entities_and_three_per_station()
    {
        var topics = _ha.DiscoveryMessages([Awake, Unknown]).Select(m => m.Topic).ToList();

        Assert.Contains("homeassistant/switch/gamingpc/automatic/config", topics);
        Assert.Contains("homeassistant/binary_sensor/gamingpc/trigger/config", topics);
        Assert.Contains("homeassistant/button/gamingpc/wake_all/config", topics);
        Assert.Contains("homeassistant/button/gamingpc/sleep_all/config", topics);
        Assert.Contains("homeassistant/switch/gamingpc/station_aabbccddee01_power/config", topics);
        Assert.Contains("homeassistant/sensor/gamingpc/station_aabbccddee01_state/config", topics);
        Assert.Contains("homeassistant/button/gamingpc/station_aabbccddee01_identify/config", topics);
        Assert.Equal(5 + 2 * 3, topics.Count);
    }

    [Fact]
    public void Station_power_switch_config_is_complete()
    {
        var message = _ha.DiscoveryMessages([Awake])
            .Single(m => m.Topic.EndsWith("station_aabbccddee01_power/config"));
        var config = JsonNode.Parse(message.Payload)!.AsObject();

        Assert.True(message.Retain);
        Assert.Equal("Power", (string?)config["name"]);
        Assert.Equal("lighthousemanager_gamingpc_station_aabbccddee01_power", (string?)config["unique_id"]);
        Assert.Equal("lighthousemanager/gamingpc/station/aabbccddee01/power", (string?)config["state_topic"]);
        Assert.Equal("lighthousemanager/gamingpc/cmd/station/aabbccddee01/power", (string?)config["command_topic"]);
        Assert.Equal("lighthousemanager/gamingpc/availability", (string?)config["availability_topic"]);

        var device = config["device"]!.AsObject();
        Assert.Equal("LHB-1A2B3C4D", (string?)device["name"]);
        Assert.Equal("lighthouse_aabbccddee01", (string?)device["identifiers"]![0]);
        Assert.Equal("lighthousemanager_gamingpc", (string?)device["via_device"]);
        Assert.Equal("aa:bb:cc:dd:ee:01", (string?)device["connections"]![0]![1]);
    }

    [Fact]
    public void Station_without_name_gets_a_readable_device_name()
    {
        var config = JsonNode.Parse(_ha.DiscoveryMessages([Unknown])
            .First(m => m.Topic.Contains("station_aabbccddee02")).Payload)!;

        Assert.Equal("Base station AA:BB:CC:DD:EE:02", (string?)config["device"]!["name"]);
    }

    [Fact]
    public void States_map_to_home_assistant_values()
    {
        var states = _ha.StateMessages(Status(Awake, Unknown with { State = StationState.Sleeping },
                new StationInfo("AA:BB:CC:DD:EE:03")))
            .ToDictionary(m => m.Topic, m => m.Payload);

        Assert.Equal("ON", states["lighthousemanager/gamingpc/automatic"]);
        Assert.Equal("ON", states["lighthousemanager/gamingpc/trigger"]);
        Assert.Equal("vrserver", (string?)JsonNode.Parse(states["lighthousemanager/gamingpc/trigger/attributes"])!["process"]);
        Assert.Equal("ON", states["lighthousemanager/gamingpc/station/aabbccddee01/power"]);
        Assert.Equal("awake", states["lighthousemanager/gamingpc/station/aabbccddee01/state"]);
        Assert.Equal("OFF", states["lighthousemanager/gamingpc/station/aabbccddee02/power"]);
        // Unknown power is "None" for Home Assistant's switch
        Assert.Equal("None", states["lighthousemanager/gamingpc/station/aabbccddee03/power"]);
        Assert.Equal(11, (int?)JsonNode.Parse(states["lighthousemanager/gamingpc/station/aabbccddee01/attributes"])!["raw_state"]);
    }

    [Theory]
    [InlineData("lighthousemanager/gamingpc/cmd/automatic", "OFF")]
    [InlineData("lighthousemanager/gamingpc/cmd/wake_all", "PRESS")]
    [InlineData("lighthousemanager/gamingpc/cmd/sleep_all", "PRESS")]
    [InlineData("lighthousemanager/gamingpc/cmd/refresh", "PRESS")]
    [InlineData("lighthousemanager/gamingpc/cmd/station/aabbccddee01/power", "ON")]
    [InlineData("lighthousemanager/gamingpc/cmd/station/aabbccddee01/identify", "PRESS")]
    public void Commands_are_recognized(string topic, string payload)
    {
        Assert.NotNull(_ha.ParseCommand(topic, payload));
    }

    [Fact]
    public void Commands_carry_their_values()
    {
        Assert.Equal(new MqttCommand.SetAutomaticMode(false), _ha.ParseCommand("lighthousemanager/gamingpc/cmd/automatic", "OFF"));
        Assert.Equal(new MqttCommand.SetStationPower(0xAABBCCDDEE01, Powerstate.Sleep),
            _ha.ParseCommand("lighthousemanager/gamingpc/cmd/station/aabbccddee01/power", "off"));
        Assert.Equal(new MqttCommand.SetAllPower(Powerstate.Wake), _ha.ParseCommand("lighthousemanager/gamingpc/cmd/wake_all", "PRESS"));
    }

    [Theory]
    // Another computer, own states, unknown commands, bad payloads and addresses
    [InlineData("lighthousemanager/otherpc/cmd/wake_all", "PRESS")]
    [InlineData("lighthousemanager/gamingpc/station/aabbccddee01/power", "ON")]
    [InlineData("lighthousemanager/gamingpc/cmd/explode", "PRESS")]
    [InlineData("lighthousemanager/gamingpc/cmd/automatic", "maybe")]
    [InlineData("lighthousemanager/gamingpc/cmd/station/nothex/power", "ON")]
    [InlineData("lighthousemanager/gamingpc/cmd/station/aabbccddee01/power", "PRESS")]
    public void Other_messages_are_ignored(string topic, string payload)
    {
        Assert.Null(_ha.ParseCommand(topic, payload));
    }

    [Theory]
    [InlineData(true, "", 1883, "homeassistant", "lighthousemanager", "", false)]
    [InlineData(false, "", 1883, "homeassistant", "lighthousemanager", "", true)]
    [InlineData(true, "broker", 0, "homeassistant", "lighthousemanager", "", false)]
    [InlineData(true, "broker", 1883, "home/#", "lighthousemanager", "", false)]
    [InlineData(true, "broker", 1883, "homeassistant", "light house", "", false)]
    [InlineData(true, "broker", 1883, "homeassistant", "/lighthousemanager", "", false)]
    [InlineData(true, "broker", 1883, "homeassistant", "lighthousemanager", "my pc", false)]
    [InlineData(true, "broker", 8883, "homeassistant", "home/lighthouse", "gaming-pc_1", true)]
    public void Settings_are_validated(bool enabled, string host, int port, string prefix, string baseTopic,
        string nodeId, bool valid)
    {
        var settings = new MqttSettings
        {
            Enabled = enabled, Host = host, Port = port, DiscoveryPrefix = prefix, BaseTopic = baseTopic,
            NodeId = nodeId
        };
        Assert.Equal(valid, settings.Validate() == null);
    }

    [Fact]
    public void Default_node_id_is_topic_safe()
    {
        Assert.Matches("^[a-z0-9_-]+$", MqttSettings.DefaultNodeId);
    }
}
