using Avalonia;
using Avalonia.Controls;

namespace LighthouseManager.Tray.Views;

/// <summary>
///     Borderless window shown next to the tray icon; hides itself when it loses focus.
/// </summary>
public partial class FlyoutWindow : Window
{
    private const int ScreenMargin = 12;

    // Room for a panel the platform doesn't report (e.g. Plasma under XWayland reports the whole screen as working
    // area)
    private const int UnknownPanelMargin = 56;

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

    /// <summary>
    ///     Tray APIs don't tell where the icon is, so place the flyout in the right corner on the side of the
    ///     screen where the panel/taskbar is (the side the working area is shrunk on).
    /// </summary>
    private void PlaceNearTray()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;

        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var scaling = screen.Scaling;
        var margin = (int)(ScreenMargin * scaling);
        var panelUnknown = work == bounds;
        var panelAtTop = work.Y > bounds.Y;
        var panelGap = panelUnknown ? (int)(UnknownPanelMargin * scaling) : margin;

        // Never taller than the screen; the station list scrolls instead. Changing MaxHeight triggers a new layout
        // and SizeChanged, which places the window again.
        var maxHeight = (work.Height - margin - panelGap) / scaling;
        if (Math.Abs(MaxHeight - maxHeight) > 0.5) MaxHeight = maxHeight;

        var size = new PixelSize((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        var x = work.Right - size.Width - margin;
        var y = panelAtTop ? work.Y + margin : work.Bottom - size.Height - panelGap;
        Position = new PixelPoint(x, Math.Max(work.Y + margin, y));
    }
}
