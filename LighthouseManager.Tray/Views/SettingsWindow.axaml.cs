using Avalonia.Controls;
using LighthouseManager.Tray.ViewModels;

namespace LighthouseManager.Tray.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(typeof(SettingsWindow).Assembly.GetManifestResourceStream("icon.png")!);
        // Reused: closing only hides
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <param name="tab">"stations", "triggers", "homeassistant" or "general"; null keeps the current tab.</param>
    public void ShowSettings(string? tab = null)
    {
        (DataContext as SettingsViewModel)?.Reload();
        if (this.FindControl<TabControl>("Tabs") is { } tabs && tab != null)
            tabs.SelectedIndex = tab switch { "triggers" => 1, "homeassistant" => 2, "general" => 3, _ => 0 };
        Show();
        Activate();
    }
}
