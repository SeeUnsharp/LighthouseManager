using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LighthouseManager.Tray.Api;
using LighthouseManager.Tray.Integration;
using Velopack;

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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasNoStations), nameof(HasStations))]
    private bool _connected;

    // Set from the API; changing it from the UI goes through SetAutomaticModeCommand
    [ObservableProperty] private bool _automaticMode;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _triggerProcess;

    [ObservableProperty] private string? _lastError;

    [ObservableProperty] private OverallState _overall = OverallState.Disconnected;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNoStations), nameof(HasStations))]
    private int _stationCount;

    [ObservableProperty] private string? _settingsFile;

    [ObservableProperty] private string[] _triggerProcesses = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _serviceVersion;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpdateText))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private UpdateInfo? _update;

    [ObservableProperty] private bool _isUpdating;

    private readonly Updates _updates;

    // Searched automatically once when no base station is configured (first start)
    private bool _autoDiscoveryDone;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowDiscovery), nameof(HasNoStations))]
    private bool _isDiscovering;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowDiscovery), nameof(HasNoStations))]
    private string? _discoveryMessage;

    /// <summary>
    ///     Base stations found by the last search that aren't configured yet.
    /// </summary>
    public ObservableCollection<FoundStationViewModel> Found { get; } = [];

    public bool HasFound => Found.Count > 0;

    public bool ShowDiscovery => IsDiscovering || HasFound || DiscoveryMessage != null;

    public MainViewModel(ApiClient api, Updates updates)
    {
        _api = api;
        _updates = updates;
    }

    /// <summary>
    ///     Installed builds can restart the service themselves (scheduled task / systemd user unit).
    /// </summary>
    public bool CanManageService { get; } = SystemIntegration.IsManaged;

    /// <summary>
    ///     Hint how to add base stations; hidden while a search runs or its results are shown.
    /// </summary>
    public bool HasNoStations => Connected && StationCount == 0 && !ShowDiscovery;

    public bool HasStations => Connected && StationCount > 0;

    public bool IsUpdateAvailable => Update != null;

    public string UpdateText => Update == null ? "" : $"Update to {Update.TargetFullRelease.Version}";

    public string VersionText => ServiceVersion == null ? "" : $"v{ServiceVersion}";

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

    public ApiClient Api => _api;

    /// <summary>
    ///     Raised by the flyout's settings button; the app opens the settings window.
    /// </summary>
    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void ShowSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    ///     Applies a status returned by a command (also arrives through the event stream).
    /// </summary>
    public void ApplyStatus(StatusSnapshot snapshot) => Apply(snapshot);

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

    /// <summary>
    ///     Looks for updates now and then every few hours, until <paramref name="ct" /> fires.
    /// </summary>
    public async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        if (!_updates.IsAvailable) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            while (!ct.IsCancellationRequested)
            {
                var update = await _updates.CheckAsync();
                Dispatcher.UIThread.Post(() => Update = update);
                await Task.Delay(TimeSpan.FromHours(6), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (Update == null) return;
        LastError = null;
        IsUpdating = true;
        try
        {
            // Restarts the app when done
            await _updates.ApplyAsync(Update);
        }
        catch (Exception ex)
        {
            LastError = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task FindStations()
    {
        if (IsDiscovering) return;
        Found.Clear();
        DiscoveryMessage = null;
        LastError = null;
        IsDiscovering = true;
        try
        {
            var result = await _api.DiscoverAsync();
            foreach (var station in result.Stations.Where(s => !s.Configured))
                Found.Add(new FoundStationViewModel(station.Address, station.Name));

            if (Found.Count == 0)
                DiscoveryMessage = result.Stations.Length > 0
                    ? "All base stations nearby are already added."
                    : "No base stations found. Make sure they are powered and within Bluetooth range.";
        }
        catch (Exception ex)
        {
            LastError = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
            OnPropertyChanged(nameof(HasFound));
            OnPropertyChanged(nameof(ShowDiscovery));
            OnPropertyChanged(nameof(HasNoStations));
        }
    }

    [RelayCommand]
    private async Task AddFound()
    {
        var addresses = Found.Where(f => f.IsSelected).Select(f => f.Address).ToList();
        if (addresses.Count == 0) return;
        LastError = null;
        try
        {
            Apply(await _api.AddStationsAsync(addresses));
            CloseDiscovery();
        }
        catch (Exception ex)
        {
            LastError = $"Adding failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseDiscovery()
    {
        Found.Clear();
        DiscoveryMessage = null;
        OnPropertyChanged(nameof(HasFound));
        OnPropertyChanged(nameof(ShowDiscovery));
        OnPropertyChanged(nameof(HasNoStations));
    }

    public async Task RemoveStationAsync(string address)
    {
        LastError = null;
        try
        {
            Apply(await _api.RemoveStationAsync(address));
        }
        catch (Exception ex)
        {
            LastError = $"Removing failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsFile == null) return;
        try
        {
            SystemIntegration.OpenFile(SettingsFile);
        }
        catch (Exception ex)
        {
            LastError = $"Can't open {SettingsFile}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestartService()
    {
        LastError = null;
        SystemIntegration.RestartService();
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
        SettingsFile = snapshot.SettingsFile;
        if (!TriggerProcesses.SequenceEqual(snapshot.TriggerProcesses)) TriggerProcesses = snapshot.TriggerProcesses;
        ServiceVersion = snapshot.Version;

        // Keep view models (and their bindings) stable, the station list only changes with the service config
        if (!Stations.Select(s => s.Address).SequenceEqual(snapshot.Stations.Select(s => s.Address)))
        {
            Stations.Clear();
            foreach (var info in snapshot.Stations) Stations.Add(new StationViewModel(info.Address, this));
        }

        for (var i = 0; i < snapshot.Stations.Length; i++) Stations[i].Apply(snapshot.Stations[i]);
        StationCount = Stations.Count;

        // First start: nothing configured yet, look for base stations right away
        if (StationCount == 0 && !_autoDiscoveryDone)
        {
            _autoDiscoveryDone = true;
            FindStationsCommand.Execute(null);
        }
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
