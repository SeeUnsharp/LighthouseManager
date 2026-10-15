using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LighthouseManager.Tray.Api;

namespace LighthouseManager.Tray.ViewModels;

public sealed partial class StationViewModel(string address, MainViewModel main) : ObservableObject
{
    public string Address { get; } = address;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _name;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText), nameof(IsAwake), nameof(IsSleeping))]
    private StationState _state;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _busy;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText), nameof(HasError))]
    private string? _error;

    public string DisplayName => Name ?? Address;

    public bool IsAwake => State is StationState.Awake or StationState.Booting;

    public bool IsSleeping => State is StationState.Sleeping or StationState.Standby;

    public bool HasError => Error != null;

    public string StateText => Busy ? "Working…"
        : Error ?? State switch
        {
            StationState.Awake => "On",
            StationState.Booting => "Starting…",
            StationState.Sleeping => "Sleeping",
            StationState.Standby => "Standby",
            _ => "Unknown"
        };

    public void Apply(StationInfo info)
    {
        Name = info.Name;
        State = info.State;
        Busy = info.Busy;
        Error = info.Error;
    }

    [RelayCommand]
    private Task Wake() => main.RunAsync(api => api.WakeAsync(Address));

    [RelayCommand]
    private Task Sleep() => main.RunAsync(api => api.SleepAsync(Address));

    [RelayCommand]
    private Task Identify() => main.RunAsync(api => api.IdentifyAsync(Address));
}
