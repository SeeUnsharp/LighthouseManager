using System.Text.Json;
using System.Text.Json.Serialization;

namespace LighthouseManager.Tray.Api;

// Client-side copies of the service's API contract (docs/API.md). Deliberately not shared with the service
// project: this app is just one of many possible API clients.

[JsonConverter(typeof(JsonStringEnumConverter<StationState>))]
public enum StationState
{
    Unknown,
    Sleeping,
    Standby,
    Booting,
    Awake
}

public sealed record StationInfo(
    string Address,
    string? Name,
    StationState State,
    byte? RawState,
    bool Busy,
    string? Error,
    DateTimeOffset? UpdatedAt);

public sealed record StatusSnapshot(
    bool AutomaticMode,
    bool TriggerActive,
    string? TriggerProcess,
    string[] TriggerProcesses,
    StationInfo[] Stations,
    string? SettingsFile = null,
    string? Version = null);

public sealed record CommandResult(bool Success, StatusSnapshot Status);

public sealed record AutomaticModeRequest(bool Enabled);

public sealed record AddStationsRequest(string[] Addresses);

public sealed record TriggerList(string[] Processes);

public sealed record ApiError(string Error);

public sealed record MqttConfig(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    bool HasPassword,
    bool UseTls,
    string DiscoveryPrefix,
    string BaseTopic,
    string NodeId,
    string DefaultNodeId,
    bool Connected,
    string? Error);

/// <param name="Password">null keeps the stored password.</param>
public sealed record MqttConfigRequest(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string? Password,
    bool UseTls,
    string? DiscoveryPrefix,
    string? BaseTopic,
    string? NodeId);

public sealed record DiscoveredStationInfo(string Address, string Name, bool Configured);

public sealed record DiscoveryResult(DiscoveredStationInfo[] Stations);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(AutomaticModeRequest))]
[JsonSerializable(typeof(AddStationsRequest))]
[JsonSerializable(typeof(TriggerList))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(MqttConfig))]
[JsonSerializable(typeof(MqttConfigRequest))]
[JsonSerializable(typeof(DiscoveryResult))]
internal partial class ApiJsonContext : JsonSerializerContext;
