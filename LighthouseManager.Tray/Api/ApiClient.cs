using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace LighthouseManager.Tray.Api;

/// <summary>
///     Client for the LighthouseManagerService HTTP API.
/// </summary>
public sealed class ApiClient(Uri baseAddress) : IDisposable
{
    // No client timeout: the service bounds every command (per-attempt timeouts and retries), and aborting the
    // request would cancel the command on the service while it may still succeed for other stations
    private readonly HttpClient _http = new() { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };

    public Uri BaseAddress => baseAddress;

    public void Dispose() => _http.Dispose();

    public Task<CommandResult> WakeAsync(string? address = null) => PostAsync(StationPath(address, "wake"));

    public Task<CommandResult> SleepAsync(string? address = null) => PostAsync(StationPath(address, "sleep"));

    public Task<CommandResult> IdentifyAsync(string address) => PostAsync(StationPath(address, "identify"));

    public Task<CommandResult> RefreshAsync() => PostAsync("api/stations/refresh");

    /// <summary>
    ///     Scans for base stations nearby; takes <paramref name="seconds" />.
    /// </summary>
    public async Task<DiscoveryResult> DiscoverAsync(int seconds = 10)
    {
        using var response = await _http.PostAsync($"api/discovery?seconds={seconds}", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.DiscoveryResult))!;
    }

    public async Task<StatusSnapshot> AddStationsAsync(IEnumerable<string> addresses)
    {
        using var response = await _http.PostAsJsonAsync("api/stations", new AddStationsRequest(addresses.ToArray()),
            ApiJsonContext.Default.AddStationsRequest);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.StatusSnapshot))!;
    }

    public async Task<StatusSnapshot> SetTriggersAsync(IEnumerable<string> processes)
    {
        using var response = await _http.PutAsJsonAsync("api/triggers", new TriggerList(processes.ToArray()),
            ApiJsonContext.Default.TriggerList);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.StatusSnapshot))!;
    }

    /// <summary>
    ///     Names of the applications running on the service's machine.
    /// </summary>
    public async Task<string[]> GetProcessesAsync()
    {
        var list = await _http.GetFromJsonAsync("api/processes", ApiJsonContext.Default.TriggerList);
        return list?.Processes ?? [];
    }

    public async Task<StatusSnapshot> RemoveStationAsync(string address)
    {
        using var response = await _http.DeleteAsync($"api/stations/{Uri.EscapeDataString(address)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.StatusSnapshot))!;
    }

    public async Task<StatusSnapshot> SetAutomaticModeAsync(bool enabled)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _http.PutAsJsonAsync("api/automatic", new AutomaticModeRequest(enabled),
            ApiJsonContext.Default.AutomaticModeRequest, cts.Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.StatusSnapshot, cts.Token))!;
    }

    /// <summary>
    ///     Streams status snapshots (Server-Sent Events) until the connection drops or <paramref name="ct" /> fires.
    /// </summary>
    public async IAsyncEnumerable<StatusSnapshot> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/events");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
        {
            if (item.EventType != "status") continue;
            var snapshot = JsonSerializer.Deserialize(item.Data, ApiJsonContext.Default.StatusSnapshot);
            if (snapshot != null) yield return snapshot;
        }
    }

    private async Task<CommandResult> PostAsync(string path)
    {
        using var response = await _http.PostAsync(path, null);
        // 502 = base station not reachable; the body still carries the current status
        if ((int)response.StatusCode != 502) response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.CommandResult))!;
    }

    private static string StationPath(string? address, string action)
    {
        return address == null ? $"api/stations/{action}" : $"api/stations/{Uri.EscapeDataString(address)}/{action}";
    }
}
