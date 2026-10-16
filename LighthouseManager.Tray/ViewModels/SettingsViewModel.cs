using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LighthouseManager.Tray.Integration;

namespace LighthouseManager.Tray.ViewModels;

/// <summary>
///     A trigger application in the settings list, with a friendly name for well known VR runtimes.
/// </summary>
public sealed record TriggerItem(string Process, string? Description)
{
    public string Label => Description == null ? Process : $"{Process}  ({Description})";
}

/// <summary>
///     Settings window: base stations, trigger applications, autostart. Base stations and discovery are shared with
///     the flyout (<see cref="MainViewModel" />).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    // Well known VR runtimes, offered as one-click triggers
    private static readonly (string Process, string Description)[] KnownRuntimes =
    [
        ("vrserver", "SteamVR"),
        ("monado-service", "Monado"),
        ("wivrn-server", "WiVRn")
    ];

    private bool _loadingAutostart;

    public SettingsViewModel(MainViewModel main)
    {
        Main = main;
        HomeAssistant = new HomeAssistantViewModel(main.Api);
        main.PropertyChanged += OnMainChanged;
        UpdateTriggers();
        LoadAutostart();
    }

    public MainViewModel Main { get; }

    // ---- Trigger applications ----

    public ObservableCollection<TriggerItem> Triggers { get; } = [];

    /// <summary>
    ///     Known runtimes that aren't triggers yet.
    /// </summary>
    public ObservableCollection<TriggerItem> SuggestedTriggers { get; } = [];

    public bool HasSuggestedTriggers => SuggestedTriggers.Count > 0;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddTriggerCommand))]
    private string _newTrigger = "";

    private string[] _runningProcesses = [];

    /// <summary>
    ///     Running applications matching <see cref="ProcessFilter" />, for picking a trigger.
    /// </summary>
    public ObservableCollection<string> FilteredProcesses { get; } = [];

    [ObservableProperty] private string _processFilter = "";

    [ObservableProperty] private bool _isProcessListVisible;

    [RelayCommand(CanExecute = nameof(CanAddTrigger))]
    private Task AddTrigger() => SaveTriggersAsync(Main.TriggerProcesses.Append(NewTrigger));

    private bool CanAddTrigger() => !string.IsNullOrWhiteSpace(NewTrigger);

    [RelayCommand]
    private Task AddProcess(string process) => SaveTriggersAsync(Main.TriggerProcesses.Append(process));

    [RelayCommand]
    private Task RemoveTrigger(TriggerItem item)
    {
        if (Main.TriggerProcesses.Length <= 1)
        {
            Main.LastError = "At least one trigger application is needed. Add another one first.";
            return Task.CompletedTask;
        }

        return SaveTriggersAsync(Main.TriggerProcesses.Where(p =>
            !p.Equals(item.Process, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private async Task ShowProcesses()
    {
        Main.LastError = null;
        try
        {
            _runningProcesses = await Main.Api.GetProcessesAsync();
            IsProcessListVisible = true;
            FilterProcesses();
        }
        catch (Exception ex)
        {
            Main.LastError = $"Can't list applications: {ex.Message}";
        }
    }

    partial void OnProcessFilterChanged(string value) => FilterProcesses();

    private void FilterProcesses()
    {
        FilteredProcesses.Clear();
        var configured = new HashSet<string>(Main.TriggerProcesses, StringComparer.OrdinalIgnoreCase);
        foreach (var process in _runningProcesses.Where(p =>
                     !configured.Contains(p) && p.Contains(ProcessFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            FilteredProcesses.Add(process);
    }

    private async Task SaveTriggersAsync(IEnumerable<string> processes)
    {
        Main.LastError = null;
        try
        {
            Main.ApplyStatus(await Main.Api.SetTriggersAsync(processes));
            NewTrigger = "";
            FilterProcesses();
        }
        catch (Exception ex)
        {
            Main.LastError = $"Saving trigger applications failed: {ex.Message}";
        }
    }

    private void UpdateTriggers()
    {
        Triggers.Clear();
        foreach (var process in Main.TriggerProcesses)
            Triggers.Add(new TriggerItem(process, Describe(process)));

        SuggestedTriggers.Clear();
        foreach (var (process, description) in KnownRuntimes)
            if (!Main.TriggerProcesses.Contains(process, StringComparer.OrdinalIgnoreCase))
                SuggestedTriggers.Add(new TriggerItem(process, description));
        OnPropertyChanged(nameof(HasSuggestedTriggers));
    }

    private static string? Describe(string process) => KnownRuntimes
        .FirstOrDefault(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase)).Description;

    // ---- General ----

    /// <summary>
    ///     Autostart can only be changed for installed builds (Setup.exe / AppImage).
    /// </summary>
    public bool CanManageAutostart { get; } = SystemIntegration.IsManaged;

    [ObservableProperty] private bool _serviceAutostart;

    [ObservableProperty] private bool _trayAutostart;

    public string LogDirectory => Path.Combine(SingleInstance.StateDirectory, "logs");

    partial void OnServiceAutostartChanged(bool value)
    {
        if (!_loadingAutostart) Apply(() => SystemIntegration.ServiceAutostart = value);
    }

    partial void OnTrayAutostartChanged(bool value)
    {
        if (!_loadingAutostart) Apply(() => SystemIntegration.TrayAutostart = value);
    }

    [RelayCommand]
    private void OpenLogs() => Apply(() => SystemIntegration.OpenFolder(LogDirectory));

    /// <summary>
    ///     Re-reads what may have changed outside the window (autostart).
    /// </summary>
    public void Reload()
    {
        LoadAutostart();
        _ = HomeAssistant.LoadAsync();
    }

    public HomeAssistantViewModel HomeAssistant { get; }

    private void LoadAutostart()
    {
        _loadingAutostart = true;
        ServiceAutostart = SystemIntegration.ServiceAutostart ?? false;
        TrayAutostart = SystemIntegration.TrayAutostart ?? false;
        _loadingAutostart = false;
    }

    private void Apply(Action action)
    {
        Main.LastError = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Main.LastError = ex.Message;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TriggerProcesses)) UpdateTriggers();
    }
}
