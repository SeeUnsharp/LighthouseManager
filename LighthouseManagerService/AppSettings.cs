using LighthouseManager.Core;

namespace LighthouseManagerService;

public class AppSettings
{
    /// <summary>
    ///     Polling interval for the trigger processes in milliseconds (minimum 1000).
    /// </summary>
    public int Interval { get; set; } = 1000;

    /// <summary>
    ///     Comma separated base station MAC addresses.
    /// </summary>
    public string BaseStationAddresses { get; set; } = "";

    /// <summary>
    ///     Comma separated process names (without extension). Base stations are woken while any of them runs.
    ///     Defaults to SteamVR's vrserver.
    /// </summary>
    public string TriggerProcesses { get; set; } = "vrserver";

    /// <summary>
    ///     Whether trigger processes wake/sleep the base stations. Can be toggled at runtime through the API.
    /// </summary>
    public bool AutomaticMode { get; set; } = true;

    /// <summary>
    ///     How often the power state of all base stations is read, in seconds. 0 disables periodic refreshes.
    /// </summary>
    public int StateRefreshInterval { get; set; } = 300;

    /// <summary>
    ///     Address of the HTTP API. Empty disables the API. Keep it on loopback: the API has no authentication.
    /// </summary>
    public string ApiUrl { get; set; } = "http://127.0.0.1:47800";

    /// <summary>
    ///     Comma separated browser origins (e.g. "http://localhost:5173") allowed to call the API. Requests from other
    ///     web pages are rejected, so a website can't control the base stations. Non-browser clients are unaffected.
    /// </summary>
    public string AllowedOrigins { get; set; } = "";

    public IReadOnlyList<string> GetAllowedOrigins()
    {
        return SplitList(AllowedOrigins);
    }

    public bool TryGetAddresses(out IReadOnlyList<ulong> addresses)
    {
        return MacAddress.TryParseList(SplitList(BaseStationAddresses), out addresses, out _);
    }

    public IReadOnlyList<string> GetTriggerProcesses() => NormalizeTriggerProcesses(SplitList(TriggerProcesses));

    /// <summary>
    ///     Trimmed, without ".exe" (process names don't have it), without empty and duplicate entries.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTriggerProcesses(IEnumerable<string> names)
    {
        return names
            .Select(p => p.Trim())
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .Where(p => p.Length > 0 && !p.Contains(','))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string[] SplitList(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
