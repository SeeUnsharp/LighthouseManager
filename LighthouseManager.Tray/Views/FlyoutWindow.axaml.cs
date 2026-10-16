using Avalonia;
using Avalonia.Controls;

namespace LighthouseManager.Tray.Views;

/// <summary>
///     Borderless window shown next to the tray icon; hides itself when it loses focus.
/// </summary>
public partial class FlyoutWindow : Window
{
    public FlyoutWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => HideFlyout();
        // The size is only final after layout (and changes with the station list), keep the flyout in the corner
        SizeChanged += (_, _) =>
        {
            if (IsVisible) PlaceNearTray();
        };
        // Closing (e.g. Alt+F4) only hides, the app lives in the tray
        Closing += (_, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };
    }

    /// <summary>
    ///     When the flyout was hidden last. Clicking the tray icon while the flyout is open first deactivates
    ///     (hides) it, so the click must not show it again right away.
    /// </summary>
    public DateTime LastHidden { get; private set; }

    public void ShowFlyout()
    {
        Show();
        PlaceNearTray();
        Activate();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        LastHidden = DateTime.UtcNow;
    }

    private void PlaceNearTray() => TrayPlacement.Place(this);
}
