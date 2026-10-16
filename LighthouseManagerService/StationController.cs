using System.Threading.Channels;
using LighthouseManager.Core;
using Microsoft.Extensions.Options;

namespace LighthouseManagerService;

/// <summary>
///     Single owner of all base station operations. The automatic trigger watcher and the HTTP API both go through
///     here, so only one Bluetooth operation runs at a time and every client sees the same state.
/// </summary>
public sealed class StationController
{
    private const string StuckMessage = "Doesn't finish starting up. Check its power and LED.";

    private readonly SemaphoreSlim _bluetoothLock = new(1, 1);
    private readonly BootWatch _bootWatch = new();
    private readonly ILogger<StationController> _logger;
    private readonly BaseStationManager _manager;
    private readonly Dictionary<ulong, StationInfo> _stations;
    private readonly List<Channel<StatusSnapshot>> _subscribers = [];
    private IReadOnlyList<string> _triggerProcesses;
    private readonly object _sync = new();

    private readonly Dictionary<CancellationTokenSource, IReadOnlyList<ulong>> _refreshes = [];
    private readonly CancellationToken _stopping;

    private CancellationTokenSource? _automaticCts;
    private bool _automaticMode;
    private string? _activeTrigger;

    public StationController(ILogger<StationController> logger, IOptions<AppSettings> settings,
        BaseStationManager manager, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _manager = manager;
        settings.Value.TryGetAddresses(out var addresses);
        _stations = addresses.ToDictionary(a => a, a => new StationInfo(MacAddress.Format(a)));
        _triggerProcesses = settings.Value.GetTriggerProcesses();
        _automaticMode = settings.Value.AutomaticMode;
    }

    /// <summary>
    ///     Configured base stations; changes at runtime through <see cref="AddStations" /> and
    ///     <see cref="RemoveStation" />.
    /// </summary>
    public IReadOnlyList<ulong> Addresses
    {
        get
        {
            lock (_sync) return _stations.Keys.ToList();
        }
    }

    public bool AutomaticMode
    {
        get
        {
            lock (_sync) return _automaticMode;
        }
        set
        {
            lock (_sync)
            {
                if (_automaticMode == value) return;
                _automaticMode = value;
                // Turning automatic mode off stops a running automatic wake/sleep
                if (!value) _automaticCts?.Cancel();
            }

            _logger.LogInformation("Automatic mode {State}", value ? "enabled" : "disabled");
            UserSettings.Save("AutomaticMode", value);
            Publish();
        }
    }

    /// <summary>
    ///     Process names that wake the base stations while running.
    /// </summary>
    public IReadOnlyList<string> TriggerProcesses
    {
        get
        {
            lock (_sync) return _triggerProcesses;
        }
    }

    /// <summary>
    ///     Replaces the trigger processes (saved to the user settings file).
    /// </summary>
    /// <returns>false if no valid process name is left.</returns>
    public bool SetTriggerProcesses(IEnumerable<string> names)
    {
        var normalized = AppSettings.NormalizeTriggerProcesses(names);
        if (normalized.Count == 0) return false;

        lock (_sync) _triggerProcesses = normalized;
        UserSettings.Save("TriggerProcesses", string.Join(",", normalized));
        _logger.LogInformation("Trigger processes: {Triggers}", string.Join(",", normalized));
        Publish();
        return true;
    }

    /// <summary>
    ///     Incremented on every manual power command; lets the watcher know its target was overridden.
    /// </summary>
    public int ManualOverrideVersion { get; private set; }

    public bool IsKnown(ulong address)
    {
        lock (_sync) return _stations.ContainsKey(address);
    }

    /// <summary>
    ///     Scans for base stations nearby (doesn't change the configuration).
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredStation>?> DiscoverAsync(TimeSpan duration, CancellationToken ct)
    {
        IReadOnlyList<DiscoveredStation> found = [];
        var success = await RunAsync([], OperationKind.Passive, ct, async token =>
        {
            found = await _manager.FindBaseStationsAsync(duration, token);
            return true;
        });
        return success ? found : null;
    }

    /// <summary>
    ///     Adds base stations to the configuration (saved to the user settings file) and reads their state.
    /// </summary>
    public void AddStations(IReadOnlyCollection<ulong> addresses)
    {
        List<ulong> added;
        lock (_sync)
        {
            added = addresses.Where(a => !_stations.ContainsKey(a)).Distinct().ToList();
            foreach (var address in added) _stations[address] = new StationInfo(MacAddress.Format(address));
        }

        if (added.Count == 0) return;
        Save();
        _logger.LogInformation("Added base station(s) {Addresses}", string.Join(",", added.Select(MacAddress.Format)));
        Publish();
        ScheduleRefresh(added, TimeSpan.Zero);
    }

    /// <returns>false if the base station isn't configured.</returns>
    public bool RemoveStation(ulong address)
    {
        lock (_sync)
        {
            if (!_stations.Remove(address)) return false;
        }

        _bootWatch.Forget(address);

        Save();
        _logger.LogInformation("Removed base station {Address}", MacAddress.Format(address));
        Publish();
        return true;
    }

    private void Save() => UserSettings.SaveBaseStationAddresses(Addresses);

    /// <summary>
    ///     Reported by the trigger watcher; null when no trigger process runs.
    /// </summary>
    public void SetActiveTrigger(string? process)
    {
        lock (_sync)
        {
            if (_activeTrigger == process) return;
            _activeTrigger = process;
        }

        Publish();
    }

    public Task<bool> SetPowerAsync(IReadOnlyList<ulong> addresses, Powerstate state, bool automatic,
        CancellationToken ct)
    {
        return RunAsync(addresses, automatic ? OperationKind.Automatic : OperationKind.Manual, ct, async token =>
        {
            // Per station, so each one shows its own result as soon as it's done
            var results = await Task.WhenAll(addresses.Select(async address =>
            {
                var success = await _manager.ChangePowerstateAsync([address], state, token);
                if (success)
                    Update([address], s => s with
                    {
                        State = state switch
                        {
                            // Freshly woken stations spin up first, the next refresh shows Awake
                            Powerstate.Wake => StationState.Booting,
                            Powerstate.Sleep => StationState.Sleeping,
                            _ => StationState.Standby
                        },
                        // The raw value is only known again after the next read
                        RawState = null, Busy = false, Error = null, UpdatedAt = DateTimeOffset.UtcNow
                    });
                else
                    Update([address], s => s with
                    {
                        Busy = false, Error = token.IsCancellationRequested ? s.Error : $"'{state}' failed"
                    });

                if (success && state == Powerstate.Wake)
                {
                    // Follow the start-up until the station reports Awake (see BootWatch)
                    _bootWatch.Woken(address, DateTime.UtcNow);
                    ScheduleRefresh([address], TimeSpan.FromSeconds(10));
                }
                else if (success)
                {
                    _bootWatch.Forget(address);
                }

                return success;
            }));

            return results.All(x => x);
        });
    }

    public Task<bool> IdentifyAsync(ulong address, CancellationToken ct)
    {
        return RunAsync([address], OperationKind.Passive, ct, async token =>
        {
            var success = await _manager.IdentifyAsync(address, token);
            // The firmware also wakes a sleeping station on identify (and it stays awake), so read the state again
            if (success) ScheduleRefresh([address], TimeSpan.FromSeconds(5));
            return success;
        });
    }

    /// <summary>
    ///     Reads the current power state of the given (default: all) base stations.
    /// </summary>
    public Task<bool> RefreshAsync(IReadOnlyList<ulong>? addresses, CancellationToken ct)
    {
        addresses ??= Addresses;
        return RunAsync(addresses, OperationKind.Refresh, ct, async token =>
        {
            // Per station, so one unreachable station doesn't hold back the others
            var readings = await Task.WhenAll(addresses.Select(async address =>
            {
                var reading = await _manager.ReadStateAsync(address, token);
                if (reading != null)
                {
                    var verdict = _bootWatch.Observe(address, reading.State, DateTime.UtcNow);
                    if (verdict == BootWatch.Verdict.Stuck)
                        _logger.LogWarning("{Address}: Still starting up after {Minutes} minutes (raw 0x{Raw:X2})",
                            MacAddress.Format(address), BootWatch.StuckAfter.TotalMinutes, reading.RawValue);
                    Update([address], s => s with
                    {
                        State = reading.State, RawState = reading.RawValue, Name = reading.Name ?? s.Name,
                        Busy = false, Error = verdict == BootWatch.Verdict.Stuck ? StuckMessage : null,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                    // Starting up: read again soon, so the UI shows "On" (or the stuck error) promptly
                    if (verdict == BootWatch.Verdict.CheckAgain) ScheduleRefresh([address], BootWatch.CheckInterval);
                }
                else
                    Update([address], s => token.IsCancellationRequested
                        ? s with { Busy = false }
                        : s with { State = StationState.Unknown, Busy = false, Error = "Not reachable" });
                return reading;
            }));

            return readings.All(r => r != null);
        });
    }

    public StatusSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new StatusSnapshot(_automaticMode, _activeTrigger != null, _activeTrigger,
                _triggerProcesses.ToArray(), _stations.Values.ToArray(), AppPaths.UserSettingsFile, AppPaths.Version);
        }
    }

    /// <summary>
    ///     Live snapshots: the current one immediately, then one after every change. Only the latest pending snapshot
    ///     is kept for slow readers.
    /// </summary>
    public async IAsyncEnumerable<StatusSnapshot> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        var channel = Channel.CreateBounded<StatusSnapshot>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_sync) _subscribers.Add(channel);

        try
        {
            yield return GetSnapshot();
            await foreach (var snapshot in channel.Reader.ReadAllAsync(ct)) yield return snapshot;
        }
        finally
        {
            lock (_sync) _subscribers.Remove(channel);
        }
    }

    private async Task<bool> RunAsync(IReadOnlyList<ulong> addresses, OperationKind kind, CancellationToken ct,
        Func<CancellationToken, Task<bool>> operation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        lock (_sync)
        {
            switch (kind)
            {
                case OperationKind.Manual:
                    // Manual commands win over a running automatic wake/sleep
                    ManualOverrideVersion++;
                    _automaticCts?.Cancel();
                    CancelRefreshes();
                    break;
                case OperationKind.Automatic:
                    if (!_automaticMode) return false;
                    _automaticCts?.Cancel();
                    _automaticCts = cts;
                    CancelRefreshes();
                    break;
                case OperationKind.Refresh:
                    _refreshes[cts] = addresses;
                    break;
            }
        }

        try
        {
            await _bluetoothLock.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Clear(cts);
            return false;
        }

        Update(addresses, s => s with { Busy = true });
        try
        {
            return await operation(cts.Token);
        }
        finally
        {
            Update(addresses, s => s with { Busy = false });
            _bluetoothLock.Release();
            Clear(cts);
        }
    }

    // Power commands must not wait behind a state read (which can take long for an unreachable station). The
    // cancelled read is repeated after the command.
    private void CancelRefreshes()
    {
        foreach (var (cts, addresses) in _refreshes)
        {
            cts.Cancel();
            ScheduleRefresh(addresses, TimeSpan.FromSeconds(10));
        }

        _refreshes.Clear();
    }

    private void Clear(CancellationTokenSource cts)
    {
        lock (_sync)
        {
            if (_automaticCts == cts) _automaticCts = null;
            _refreshes.Remove(cts);
        }
    }

    private void ScheduleRefresh(IReadOnlyList<ulong> addresses, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _stopping);
                await RefreshAsync(addresses, _stopping);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping
            }
        });
    }

    private void Update(IEnumerable<ulong> addresses, Func<StationInfo, StationInfo> change)
    {
        lock (_sync)
        {
            foreach (var address in addresses)
                if (_stations.TryGetValue(address, out var station))
                    _stations[address] = change(station);
        }

        Publish();
    }

    private void Publish()
    {
        // Snapshot and write under one lock: otherwise a concurrent publisher could write an older snapshot after a
        // newer one, and the latest-only channels would drop the newer one
        lock (_sync)
        {
            var snapshot = GetSnapshot();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(snapshot);
        }
    }

    private enum OperationKind
    {
        /// <summary>From the trigger watcher; cancelled by manual commands or disabling automatic mode.</summary>
        Automatic,

        /// <summary>Power command from a client; cancels a running automatic operation.</summary>
        Manual,

        /// <summary>State read; cancelled by power commands.</summary>
        Refresh,

        /// <summary>Identify; doesn't interfere with other operations, just waits its turn.</summary>
        Passive
    }
}

public sealed record StationInfo(
    string Address,
    string? Name = null,
    StationState State = StationState.Unknown,
    byte? RawState = null,
    bool Busy = false,
    string? Error = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record StatusSnapshot(
    bool AutomaticMode,
    bool TriggerActive,
    string? TriggerProcess,
    string[] TriggerProcesses,
    StationInfo[] Stations,
    string SettingsFile,
    string Version);
