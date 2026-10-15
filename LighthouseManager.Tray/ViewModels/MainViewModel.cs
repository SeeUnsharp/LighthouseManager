using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LighthouseManager.Tray.Api;

namespace LighthouseManager.Tray.ViewModels;

/// <summary>
///     Overall state shown by the tray icon.
/// </summary>
public enum OverallState
{
    Disconnected,
    Sleeping,
    Busy,
    Awake,
    Error
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _connected;

    // Set from the API; changing it from the UI goes through SetAutomaticModeCommand
    [ObservableProperty] private bool _automaticMode;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _triggerProcess;

    [ObservableProperty] private string? _lastError;

    [ObservableProperty] private OverallState _overall = OverallState.Disconnected;

    public MainViewModel(ApiClient api)
    {
        _api = api;
    }

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    /// <summary>
    ///     Raised after every status update or connection change (also when only a single station changed).
    /// </summary>
    public event EventHandler? Changed;

    public string StatusText => !Connected ? $"Service not reachable ({_api.BaseAddress.Authority})"
        : TriggerProcess != null ? $"In use by {TriggerProcess}"
        : "No trigger process running";

    /// <summary>
    ///     Follows the service's live events, reconnecting until <paramref name="ct" /> fires.
    /// </summary>
    public async Task RunEventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in _api.SubscribeAsync(ct))
                    Dispatcher.UIThread.Post(() => Apply(snapshot));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Service not running (yet) or restarted
            }

            Dispatcher.UIThread.Post(() =>
            {
                Connected = false;
                UpdateOverall();
            });

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Runs a command; the resulting state arrives through the event stream as well.
    /// </summary>
    public async Task RunAsync(Func<ApiClient, Task<CommandResult>> command)
    {
        LastError = null;
        try
        {
            var result = await command(_api);
            Apply(result.Status);
            if (!result.Success) LastError = "Command failed, base station not reachable?";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    [RelayCommand]
    private Task WakeAll() => RunAsync(api => api.WakeAsync());

    [RelayCommand]
    private Task SleepAll() => RunAsync(api => api.SleepAsync());

    [RelayCommand]
    private Task Refresh() => RunAsync(api => api.RefreshAsync());

    /// <param name="enabled">The switch's new state; not derived from <see cref="AutomaticMode" />, which only
    ///     changes once the service confirmed, so quick double clicks would send the same value twice.</param>
    [RelayCommand]
    private async Task SetAutomaticMode(bool enabled)
    {
        LastError = null;
        try
        {
            Apply(await _api.SetAutomaticModeAsync(enabled));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // The toggle switch already flipped itself, flip it back
            OnPropertyChanged(nameof(AutomaticMode));
        }
    }

    private void Apply(StatusSnapshot snapshot)
    {
        Connected = true;
        AutomaticMode = snapshot.AutomaticMode;
        TriggerProcess = snapshot.TriggerProcess;

        // Keep view models (and their bindings) stable, the station list only changes with the service config
        if (!Stations.Select(s => s.Address).SequenceEqual(snapshot.Stations.Select(s => s.Address)))
        {
            Stations.Clear();
            foreach (var info in snapshot.Stations) Stations.Add(new StationViewModel(info.Address, this));
        }

        for (var i = 0; i < snapshot.Stations.Length; i++) Stations[i].Apply(snapshot.Stations[i]);
        UpdateOverall();
    }

    private void UpdateOverall()
    {
        Overall = !Connected ? OverallState.Disconnected
            : Stations.Any(s => s.Busy || s.State == StationState.Booting) ? OverallState.Busy
            : Stations.Any(s => s.HasError) ? OverallState.Error
            : Stations.Any(s => s.IsAwake) ? OverallState.Awake
            : OverallState.Sleeping;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
