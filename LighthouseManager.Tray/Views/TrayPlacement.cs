using Avalonia;
using Avalonia.Controls;

namespace LighthouseManager.Tray.Views;

/// <summary>
///     Places a borderless window next to the tray (flyout, update notice).
/// </summary>
internal static class TrayPlacement
{
    private const int ScreenMargin = 12;

    // Room for a panel the platform doesn't report (e.g. Plasma under XWayland reports the whole screen as working
    // area)
    private const int UnknownPanelMargin = 56;

    /// <summary>
    ///     Tray APIs don't tell where the icon is, so the window goes to the right corner on the side of the screen
    ///     where the panel/taskbar is (the side the working area is shrunk on). It is never taller than the screen:
    ///     changing MaxHeight triggers a new layout and SizeChanged, callers place the window again then.
    /// </summary>
    public static void Place(Window window)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen == null) return;

        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var scaling = screen.Scaling;
        var margin = (int)(ScreenMargin * scaling);
        var panelUnknown = work == bounds;
        var panelAtTop = work.Y > bounds.Y;
        var panelGap = panelUnknown ? (int)(UnknownPanelMargin * scaling) : margin;

        var maxHeight = (work.Height - margin - panelGap) / scaling;
        if (Math.Abs(window.MaxHeight - maxHeight) > 0.5) window.MaxHeight = maxHeight;

        var size = new PixelSize((int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
        var x = work.Right - size.Width - margin;
        var y = panelAtTop ? work.Y + margin : work.Bottom - size.Height - panelGap;
        window.Position = new PixelPoint(x, Math.Max(work.Y + margin, y));
    }
}
