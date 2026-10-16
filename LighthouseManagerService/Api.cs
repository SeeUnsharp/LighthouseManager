using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json.Serialization;
using LighthouseManager.Core;
using LighthouseManagerService.Mqtt;

namespace LighthouseManagerService;

/// <summary>
///     Local HTTP API for UIs and other clients, documented in docs/API.md.
/// </summary>
public static class Api
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (StationController c) => c.GetSnapshot());

        api.MapGet("/events", (StationController c, CancellationToken ct) =>
            TypedResults.ServerSentEvents(c.SubscribeAsync(ct), "status"));

        api.MapPut("/automatic", (AutomaticModeRequest request, StationController c) =>
        {
            c.AutomaticMode = request.Enabled;
            return c.GetSnapshot();
        });

        api.MapGet("/stations", (StationController c) => c.GetSnapshot().Stations);

        api.MapGet("/mqtt", (MqttBridge mqtt) => ToMqttConfig(mqtt));

        api.MapPut("/mqtt", (MqttConfigRequest request, MqttBridge mqtt) =>
        {
            var current = mqtt.Settings;
            var settings = current with
            {
                Enabled = request.Enabled,
                // Missing fields arrive as null despite the record's types; validation reports a missing host
                Host = request.Host?.Trim() ?? "",
                Port = request.Port,
                Username = request.Username?.Trim() ?? "",
                // null keeps the stored password; the API never returns it
                Password = request.Password ?? current.Password,
                UseTls = request.UseTls,
                DiscoveryPrefix = request.DiscoveryPrefix?.Trim() ?? current.DiscoveryPrefix,
                BaseTopic = request.BaseTopic?.Trim() ?? current.BaseTopic,
                NodeId = request.NodeId?.Trim() ?? current.NodeId
            };
            if (settings.Validate() is { } error)
                return TypedResults.Json(new ApiError(error), ApiJsonContext.Default.ApiError,
                    statusCode: StatusCodes.Status400BadRequest);

            mqtt.Apply(settings);
            return Results.Ok(ToMqttConfig(mqtt));
        });

        api.MapGet("/triggers", (StationController c) => new TriggerList(c.TriggerProcesses.ToArray()));

        api.MapPut("/triggers", (TriggerList request, StationController c) =>
            c.SetTriggerProcesses(request.Processes) ? Results.Ok(c.GetSnapshot()) : Results.BadRequest());

        // Running applications, so UIs can offer them as trigger processes
        api.MapGet("/processes", () => new TriggerList(RunningProcessNames()));

        api.MapPost("/discovery", async (StationController c, CancellationToken ct, int? seconds) =>
        {
            var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 3, 60));
            var found = await c.DiscoverAsync(duration, ct);
            if (found == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return TypedResults.Ok(new DiscoveryResult(found.Select(f =>
                new DiscoveredStationInfo(MacAddress.Format(f.Address), f.Name, c.IsKnown(f.Address))).ToArray()));
        });

        api.MapPost("/stations", (AddStationsRequest request, StationController c) =>
        {
            if (!MacAddress.TryParseList(request.Addresses, out var addresses, out _) || addresses.Count == 0)
                return Results.BadRequest();
            c.AddStations(addresses.ToList());
            return TypedResults.Ok(c.GetSnapshot());
        });

        api.MapDelete("/stations/{address}", (string address, StationController c) =>
        {
            if (!MacAddress.TryParse(address, out var parsed)) return Results.BadRequest();
            return c.RemoveStation(parsed) ? TypedResults.Ok(c.GetSnapshot()) : Results.NotFound();
        });

        api.MapPost("/stations/refresh", async (StationController c, CancellationToken ct) =>
            ToResult(await c.RefreshAsync(null, ct), c));

        api.MapPost("/stations/wake", async (StationController c, CancellationToken ct) =>
            ToResult(await c.SetPowerAsync(c.Addresses, Powerstate.Wake, false, ct), c));

        api.MapPost("/stations/sleep", async (StationController c, CancellationToken ct) =>
            ToResult(await c.SetPowerAsync(c.Addresses, Powerstate.Sleep, false, ct), c));

        api.MapPost("/stations/{address}/wake", (string address, StationController c, CancellationToken ct) =>
            ForStation(address, c, a => c.SetPowerAsync([a], Powerstate.Wake, false, ct)));

        api.MapPost("/stations/{address}/sleep", (string address, StationController c, CancellationToken ct) =>
            ForStation(address, c, a => c.SetPowerAsync([a], Powerstate.Sleep, false, ct)));

        api.MapPost("/stations/{address}/identify", (string address, StationController c, CancellationToken ct) =>
            ForStation(address, c, a => c.IdentifyAsync(a, ct)));

        api.MapPost("/stations/{address}/refresh", (string address, StationController c, CancellationToken ct) =>
            ForStation(address, c, a => c.RefreshAsync([a], ct)));
    }

    /// <summary>
    ///     Rejects requests that browsers send on behalf of other web pages (CSRF) and DNS rebinding attempts, so a
    ///     website can't control the base stations through the unauthenticated loopback API.
    /// </summary>
    public static IApplicationBuilder UseLocalRequestGuard(this IApplicationBuilder app,
        IReadOnlyCollection<string> allowedOrigins)
    {
        return app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            var isLoopbackHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                 (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
            var origin = context.Request.Headers.Origin.ToString();

            if (!isLoopbackHost ||
                (origin.Length > 0 && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
    }

    private static MqttConfig ToMqttConfig(MqttBridge mqtt)
    {
        var s = mqtt.Settings;
        var status = mqtt.Status;
        return new MqttConfig(s.Enabled, s.Host, s.Port, s.Username, s.Password.Length > 0, s.UseTls,
            s.DiscoveryPrefix, s.BaseTopic, s.NodeId, MqttSettings.DefaultNodeId, status.Connected, status.Error);
    }

    private static string[] RunningProcessNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Length > 0) names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating
                }
            }
        }

        return names.ToArray();
    }

    private static async Task<IResult> ForStation(string address, StationController c,
        Func<ulong, Task<bool>> action)
    {
        if (!MacAddress.TryParse(address, out var parsed)) return TypedResults.BadRequest();
        if (!c.IsKnown(parsed)) return TypedResults.NotFound();
        return ToResult(await action(parsed), c);
    }

    private static IResult ToResult(bool success, StationController c)
    {
        var result = new CommandResult(success, c.GetSnapshot());
        // 502: the base station (upstream device) could not be reached or didn't accept the command
        return success
            ? TypedResults.Ok(result)
            : TypedResults.Json(result, ApiJsonContext.Default.CommandResult,
                statusCode: StatusCodes.Status502BadGateway);
    }
}

/// <summary>
///     Server used when the API is disabled (empty AppSettings:ApiUrl).
/// </summary>
internal sealed class NoListenerServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}

public sealed record AutomaticModeRequest(bool Enabled);

public sealed record AddStationsRequest(string[] Addresses);

public sealed record TriggerList(string[] Processes);

public sealed record ApiError(string Error);

/// <param name="HasPassword">Whether a password is stored; the password itself is never returned.</param>
/// <param name="DefaultNodeId">Node ID used when <paramref name="NodeId" /> is empty (from the computer name).</param>
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

/// <param name="Password">New password; null keeps the stored one, "" removes it.</param>
/// <param name="DiscoveryPrefix">null keeps the current value (also for BaseTopic and NodeId).</param>
public sealed record MqttConfigRequest(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string? Password,
    bool UseTls,
    string? DiscoveryPrefix = null,
    string? BaseTopic = null,
    string? NodeId = null);

/// <param name="Configured">Already in the configuration.</param>
public sealed record DiscoveredStationInfo(string Address, string Name, bool Configured);

public sealed record DiscoveryResult(DiscoveredStationInfo[] Stations);

public sealed record CommandResult(bool Success, StatusSnapshot Status);

/// <summary>
///     Enum values as camelCase strings ("sleeping", "awake", ...).
/// </summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    Converters = [typeof(CamelCaseEnumConverter<StationState>)])]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(StationInfo[]))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(AutomaticModeRequest))]
[JsonSerializable(typeof(AddStationsRequest))]
[JsonSerializable(typeof(TriggerList))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(MqttConfig))]
[JsonSerializable(typeof(MqttConfigRequest))]
[JsonSerializable(typeof(DiscoveryResult))]
internal partial class ApiJsonContext : JsonSerializerContext;
