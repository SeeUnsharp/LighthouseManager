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

    private static readonly Dictionary<(OverallState, bool), WindowIcon> Cache = new();

    /// <param name="updateAvailable">Adds a badge with a refresh arrow in the lower right corner.</param>
    public static WindowIcon Get(OverallState state, bool updateAvailable = false)
    {
        if (Cache.TryGetValue((state, updateAvailable), out var icon)) return icon;

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
            if (updateAvailable) DrawUpdateBadge(ctx);
        }

        icon = new WindowIcon(bitmap);
        Cache[(state, updateAvailable)] = icon;
        return icon;
    }

    /// <summary>
    ///     Blue circle with a white circular arrow, in the lower right corner (over the body, not the lens).
    /// </summary>
    private static void DrawUpdateBadge(DrawingContext ctx)
    {
        var center = new Point(46, 46);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#0969DA")), new Pen(Brushes.Black, 3), center, 17, 17);

        // Circular arrow: a 260° arc running clockwise, gap at the top right, arrowhead pointing along the circle
        const double radius = 9;
        const double startAngle = 20, endAngle = 280;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(PointOnCircle(center, radius, startAngle), false);
            g.ArcTo(PointOnCircle(center, radius, endAngle), new Size(radius, radius), 0, true,
                SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, new Pen(Brushes.White, 4, lineCap: PenLineCap.Round), arc);

        // Tangent (direction of travel) and radial direction at the arc's end
        var rad = endAngle * Math.PI / 180;
        var tangent = new Vector(-Math.Sin(rad), Math.Cos(rad));
        var radial = new Vector(Math.Cos(rad), Math.Sin(rad));
        var tip = PointOnCircle(center, radius, endAngle);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(tip + tangent * 7, true);
            g.LineTo(tip + radial * 6.5);
            g.LineTo(tip - radial * 6.5);
            g.EndFigure(true);
        }

        ctx.DrawGeometry(Brushes.White, null, head);
    }

    // Angle in degrees, 0 = right, clockwise (screen coordinates)
    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
