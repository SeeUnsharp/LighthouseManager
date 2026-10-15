using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LighthouseManager.Tray.ViewModels;

namespace LighthouseManager.Tray;

/// <summary>
///     Draws the tray icon at runtime: a base station silhouette whose lens shows the overall state.
/// </summary>
internal static class TrayIconRenderer
{
    private const int Size = 64;

    private static readonly Dictionary<OverallState, WindowIcon> Cache = new();

    public static WindowIcon Get(OverallState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;

        var lens = state switch
        {
            OverallState.Awake => Color.Parse("#2EA043"),
            OverallState.Busy => Color.Parse("#D29922"),
            OverallState.Error => Color.Parse("#DA3633"),
            OverallState.Sleeping => Color.Parse("#8B949E"),
            _ => Color.Parse("#484F58")
        };

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            // Body
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#E6EDF3")), new Pen(Brushes.Black, 3),
                new RoundedRect(new Rect(6, 6, 52, 52), 12));
            // Lens
            ctx.DrawEllipse(new SolidColorBrush(lens), new Pen(Brushes.Black, 3), new Point(32, 32), 15, 15);
            if (state == OverallState.Disconnected)
                ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#DA3633")), 6), new Point(14, 50),
                    new Point(50, 14));
        }

        icon = new WindowIcon(bitmap);
        Cache[state] = icon;
        return icon;
    }
}
