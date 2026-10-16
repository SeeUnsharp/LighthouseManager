using System.Text.Json.Nodes;
using LighthouseManager.Core;

namespace LighthouseManagerService.Mqtt;

public sealed record MqttMessage(string Topic, string Payload, bool Retain = true);

/// <summary>
///     A command received from Home Assistant.
/// </summary>
public abstract record MqttCommand
{
    public sealed record SetAutomaticMode(bool Enabled) : MqttCommand;

    public sealed record SetAllPower(Powerstate State) : MqttCommand;

    public sealed record SetStationPower(ulong Address, Powerstate State) : MqttCommand;

    public sealed record Identify(ulong Address) : MqttCommand;

    public sealed record Refresh : MqttCommand;
}

/// <summary>
///     Topics, Home Assistant MQTT discovery payloads, state messages and command parsing. Pure logic (no network),
///     see <see cref="MqttBridge" /> for the connection.
/// </summary>
/// <remarks>
///     One Home Assistant device for this computer (automatic mode, trigger, all-stations buttons) and one per base
///     station (power switch, state, identify), linked to the computer's device. Commands live under
///     <c>{base}/cmd/…</c>, separate from the retained states, so the bridge never receives its own states as commands.
/// </remarks>
public sealed class HomeAssistantMqtt(MqttSettings settings, string version)
{
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Online = "online";
    public const string Offline = "offline";

    private static readonly string[] StateOptions = ["unknown", "sleeping", "standby", "booting", "awake"];

    public string NodeId { get; } = settings.EffectiveNodeId;

    public string BaseTopic => $"{settings.BaseTopic}/{NodeId}";

    public string AvailabilityTopic => $"{BaseTopic}/availability";

    /// <summary>
    ///     Subscription for all commands.
    /// </summary>
    public string CommandFilter => $"{BaseTopic}/cmd/#";

    private string ComputerDeviceId => $"lighthousemanager_{NodeId}";

    /// <summary>
    ///     Discovery configs for the computer and the given stations (retained).
    /// </summary>
    public IReadOnlyList<MqttMessage> DiscoveryMessages(IEnumerable<StationInfo> stations)
    {
        var computer = ComputerDevice();
        var messages = new List<MqttMessage>
        {
            Config("switch", "automatic", computer, "Automatic mode", new JsonObject
            {
                ["state_topic"] = $"{BaseTopic}/automatic",
                ["command_topic"] = $"{BaseTopic}/cmd/automatic",
                ["payload_on"] = On, ["payload_off"] = Off,
                ["icon"] = "mdi:robot"
            }),
            Config("binary_sensor", "trigger", computer, "VR app running", new JsonObject
            {
                ["state_topic"] = $"{BaseTopic}/trigger",
                ["json_attributes_topic"] = $"{BaseTopic}/trigger/attributes",
                ["payload_on"] = On, ["payload_off"] = Off,
                ["device_class"] = "running",
                ["icon"] = "mdi:virtual-reality"
            }),
            Config("button", "wake_all", computer, "Turn all on", new JsonObject
            {
                ["command_topic"] = $"{BaseTopic}/cmd/wake_all", ["icon"] = "mdi:power"
            }),
            Config("button", "sleep_all", computer, "Sleep all", new JsonObject
            {
                ["command_topic"] = $"{BaseTopic}/cmd/sleep_all", ["icon"] = "mdi:power-sleep"
            }),
            Config("button", "refresh", computer, "Refresh", new JsonObject
            {
                ["command_topic"] = $"{BaseTopic}/cmd/refresh",
                ["entity_category"] = "diagnostic", ["icon"] = "mdi:refresh"
            })
        };

        foreach (var station in stations)
        {
            if (!MacAddress.TryParse(station.Address, out var address)) continue;
            var key = Key(address);
            var device = StationDevice(station, address);
            messages.Add(Config("switch", $"station_{key}_power", device, "Power", new JsonObject
            {
                ["state_topic"] = $"{BaseTopic}/station/{key}/power",
                ["command_topic"] = $"{BaseTopic}/cmd/station/{key}/power",
                ["payload_on"] = On, ["payload_off"] = Off,
                ["icon"] = "mdi:lighthouse"
            }));
            messages.Add(Config("sensor", $"station_{key}_state", device, "State", new JsonObject
            {
                ["state_topic"] = $"{BaseTopic}/station/{key}/state",
                ["json_attributes_topic"] = $"{BaseTopic}/station/{key}/attributes",
                ["device_class"] = "enum",
                ["options"] = new JsonArray(StateOptions.Select(o => (JsonNode)o).ToArray()),
                ["icon"] = "mdi:lighthouse-on"
            }));
            messages.Add(Config("button", $"station_{key}_identify", device, "Identify", new JsonObject
            {
                ["command_topic"] = $"{BaseTopic}/cmd/station/{key}/identify",
                ["device_class"] = "identify"
            }));
        }

        return messages;
    }

    /// <summary>
    ///     Retained states for a status snapshot.
    /// </summary>
    public IReadOnlyList<MqttMessage> StateMessages(StatusSnapshot status)
    {
        var messages = new List<MqttMessage>
        {
            new($"{BaseTopic}/automatic", status.AutomaticMode ? On : Off),
            new($"{BaseTopic}/trigger", status.TriggerActive ? On : Off),
            new($"{BaseTopic}/trigger/attributes", new JsonObject { ["process"] = status.TriggerProcess }.ToJsonString())
        };

        foreach (var station in status.Stations)
        {
            if (!MacAddress.TryParse(station.Address, out var address)) continue;
            var key = Key(address);
            // "None" is Home Assistant's "unknown" for a switch
            var power = station.State switch
            {
                StationState.Awake or StationState.Booting => On,
                StationState.Sleeping or StationState.Standby => Off,
                _ => "None"
            };
            messages.Add(new MqttMessage($"{BaseTopic}/station/{key}/power", power));
            messages.Add(new MqttMessage($"{BaseTopic}/station/{key}/state", StateName(station.State)));
            messages.Add(new MqttMessage($"{BaseTopic}/station/{key}/attributes", new JsonObject
            {
                ["address"] = station.Address,
                ["raw_state"] = station.RawState,
                ["busy"] = station.Busy,
                ["error"] = station.Error,
                ["updated_at"] = station.UpdatedAt?.ToString("O")
            }.ToJsonString()));
        }

        return messages;
    }

    /// <param name="retained">
    ///     The broker delivered a stored message. Commands are only taken live: a retained command would otherwise run
    ///     again on every connect (e.g. wake a station at each service start).
    /// </param>
    /// <returns>The command, or null if the topic/payload isn't one.</returns>
    public MqttCommand? ParseCommand(string topic, string payload, bool retained = false)
    {
        var prefix = $"{BaseTopic}/cmd/";
        if (retained || !topic.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = topic[prefix.Length..].Split('/');
        var on = payload.Trim().Equals(On, StringComparison.OrdinalIgnoreCase);
        var off = payload.Trim().Equals(Off, StringComparison.OrdinalIgnoreCase);

        return parts switch
        {
            ["automatic"] when on || off => new MqttCommand.SetAutomaticMode(on),
            ["wake_all"] => new MqttCommand.SetAllPower(Powerstate.Wake),
            ["sleep_all"] => new MqttCommand.SetAllPower(Powerstate.Sleep),
            ["refresh"] => new MqttCommand.Refresh(),
            ["station", var key, "power"] when (on || off) && TryParseKey(key, out var address) =>
                new MqttCommand.SetStationPower(address, on ? Powerstate.Wake : Powerstate.Sleep),
            ["station", var key, "identify"] when TryParseKey(key, out var address) => new MqttCommand.Identify(address),
            _ => null
        };
    }

    private MqttMessage Config(string component, string objectId, JsonObject device, string name, JsonObject config)
    {
        config["name"] = name;
        config["has_entity_name"] = true;
        config["unique_id"] = $"lighthousemanager_{NodeId}_{objectId}";
        config["availability_topic"] = AvailabilityTopic;
        config["payload_available"] = Online;
        config["payload_not_available"] = Offline;
        config["device"] = device.DeepClone();
        config["origin"] = new JsonObject
        {
            ["name"] = "LighthouseManager", ["sw_version"] = version,
            ["support_url"] = "https://github.com/SeeUnsharp/LighthouseManager"
        };
        return new MqttMessage($"{settings.DiscoveryPrefix}/{component}/{NodeId}/{objectId}/config",
            config.ToJsonString());
    }

    private JsonObject ComputerDevice() => new()
    {
        ["identifiers"] = new JsonArray(ComputerDeviceId),
        ["name"] = $"LighthouseManager ({Environment.MachineName})",
        ["manufacturer"] = "LighthouseManager",
        ["model"] = "Lighthouse base station control",
        ["sw_version"] = version
    };

    private JsonObject StationDevice(StationInfo station, ulong address) => new()
    {
        ["identifiers"] = new JsonArray($"lighthouse_{Key(address)}"),
        ["connections"] = new JsonArray(new JsonArray("bluetooth", MacAddress.Format(address).ToLowerInvariant())),
        ["name"] = station.Name ?? $"Base station {station.Address}",
        ["manufacturer"] = "Valve",
        ["model"] = "Lighthouse 2.0 base station",
        ["via_device"] = ComputerDeviceId
    };

    // Same strings as the HTTP API (camelCase enum values)
    private static string StateName(StationState state) => state.ToString().ToLowerInvariant();

    private static string Key(ulong address) => address.ToString("x12");

    private static bool TryParseKey(string key, out ulong address) => MacAddress.TryParse(key, out address);
}
