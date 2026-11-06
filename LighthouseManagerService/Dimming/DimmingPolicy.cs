namespace LighthouseManagerService.Dimming;

/// <summary>
///     Decides when to dim the displays (a VR game is shown) and when to restore them. Pure logic, see
///     <see cref="DisplayDimmer" /> for the polling and the brightness changes.
/// </summary>
/// <param name="restoreDelay">
///     How long no game must be shown before restoring, so switching games or a game restarting its session doesn't
///     flash the displays.
/// </param>
/// <param name="retryDelay">Wait after a failed change before trying again.</param>
/// <param name="dimmed">Whether the displays are dimmed already (left dimmed by a previous run).</param>
public sealed class DimmingPolicy(TimeSpan restoreDelay, TimeSpan retryDelay, bool dimmed = false)
{
    private DateTime? _noGameSince;
    private DateTime _retryAt = DateTime.MinValue;

    // A dim failed after changing some displays: dimmed for restoring, but dimming isn't finished
    private bool _incomplete;

    /// <summary>
    ///     Whether displays are (or may be) dimmed and need restoring.
    /// </summary>
    public bool Dimmed { get; private set; } = dimmed;

    /// <returns>true to dim, false to restore, null for nothing to do.</returns>
    public bool? Next(bool enabled, bool gameShown, DateTime now)
    {
        if (now < _retryAt) return null;

        if (enabled && gameShown)
        {
            _noGameSince = null;
            return Dimmed && !_incomplete ? null : true;
        }

        if (!Dimmed) return null;
        // Turning the feature off restores right away
        if (!enabled) return false;

        _noGameSince ??= now;
        return now - _noGameSince >= restoreDelay ? false : null;
    }

    /// <summary>
    ///     The change returned by <see cref="Next" /> succeeded.
    /// </summary>
    public void Applied(bool dimmed)
    {
        Dimmed = dimmed;
        _incomplete = false;
        _noGameSince = null;
    }

    /// <summary>
    ///     The change returned by <see cref="Next" /> failed: try again after the retry delay.
    /// </summary>
    /// <param name="partlyDimmed">
    ///     A dim failed after it may have changed displays: they're restored like dimmed ones once the game ends, and
    ///     dimming is tried again while it runs.
    /// </param>
    public void Failed(DateTime now, bool partlyDimmed = false)
    {
        _retryAt = now + retryDelay;
        if (!partlyDimmed) return;
        Dimmed = true;
        _incomplete = true;
    }
}
