namespace LighthouseManager.Tray.ViewModels;

/// <summary>
///     The "Dim displays during VR" slider: position 0 is off, 1 is KDE's own brightness change (one call, no fade of
///     ours), then fades in 0.5 s steps up to 10 s. The service keeps on/off and the fade separately.
/// </summary>
public static class DimmingSlider
{
    private const double Step = 0.5;
    private const double MaxFadeSeconds = 10;

    public const int Maximum = 1 + (int)(MaxFadeSeconds / Step);

    public static int FromStatus(bool enabled, double fadeSeconds)
    {
        return enabled ? 1 + (int)Math.Round(Math.Clamp(fadeSeconds, 0, MaxFadeSeconds) / Step) : 0;
    }

    /// <returns>The request values; FadeSeconds null keeps the service's fade (switching off).</returns>
    public static (bool Enabled, double? FadeSeconds) ToRequest(int position)
    {
        return position <= 0 ? (false, null) : (true, Math.Min(position - 1, Maximum - 1) * Step);
    }

    public static string Text(int position)
    {
        return position switch
        {
            <= 0 => "Off",
            1 => "KDE default",
            _ => $"{ToRequest(position).FadeSeconds:0.#} s"
        };
    }
}
