using CommunityToolkit.Mvvm.ComponentModel;

namespace LighthouseManager.Tray.ViewModels;

/// <summary>
///     A base station found by discovery, offered for adding.
/// </summary>
public sealed partial class FoundStationViewModel(string address, string name) : ObservableObject
{
    public string Address { get; } = address;

    public string Name { get; } = name;

    public string Label => $"{Name}  ({Address})";

    [ObservableProperty] private bool _isSelected = true;
}
