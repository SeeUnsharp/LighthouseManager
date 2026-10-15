using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json.Serialization;
using LighthouseManager.Core;

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
internal partial class ApiJsonContext : JsonSerializerContext;
