using LighthouseManager.Core;

namespace LighthouseManagerService;

/// <summary>
///     Decides when the trigger watcher wakes or sleeps the base stations. Pure logic without I/O, see
///     <see cref="Worker" /> for the polling and <see cref="StationController" /> for the execution.
/// </summary>
/// <remarks>
///     The watcher only acts on trigger state <em>changes</em>: a target counts as handled once it was applied,
///     overridden by a manual command, or skipped while automatic mode was off. Failed automatic jobs are retried,
///     but never after manual control took over.
/// </remarks>
public sealed class AutomationPolicy(int maxAttempts, TimeSpan retryDelay)
{
    private int _attempts;
    private Powerstate? _handled;
    private DateTime _nextAttempt;
    private int _overrideVersion;
    private bool _triggerSeen;

    // Whether the current target may still be applied automatically (no manual override since it was set)
    private bool _owned;

    /// <summary>
    ///     Called on every poll.
    /// </summary>
    /// <param name="triggerRunning">Whether a trigger process runs.</param>
    /// <param name="automaticMode">Whether automatic mode is on.</param>
    /// <param name="manualOverrideVersion">Incremented by every manual power command.</param>
    /// <param name="lastJobFailed">The last automatic job finished unsuccessfully.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The power state to apply now, or null.</returns>
    public Powerstate? Next(bool triggerRunning, bool automaticMode, int manualOverrideVersion, bool lastJobFailed,
        DateTime now)
    {
        _triggerSeen |= triggerRunning;

        // Nothing to do until a trigger process ran at least once
        Powerstate? desired = triggerRunning ? Powerstate.Wake : _triggerSeen ? Powerstate.Sleep : null;

        if (!automaticMode || manualOverrideVersion != _overrideVersion)
        {
            // Manual control wins until the trigger state changes again
            _overrideVersion = manualOverrideVersion;
            _handled = desired;
            _owned = false;
            return null;
        }

        if (desired is not { } state) return null;

        if (state != _handled)
        {
            _handled = state;
            _owned = true;
            _attempts = 1;
            return state;
        }

        if (_owned && lastJobFailed && _attempts < maxAttempts && now >= _nextAttempt)
        {
            _attempts++;
            return state;
        }

        return null;
    }

    /// <summary>
    ///     Reports that the last automatic job failed. Call before <see cref="Next" />, so the retry waits for the
    ///     retry delay.
    /// </summary>
    public void ReportFailure(DateTime now)
    {
        _nextAttempt = now + retryDelay;
    }

    /// <summary>
    ///     Whether a failed job for the current target will be retried (no manual override, attempts left).
    /// </summary>
    public bool WillRetry => _owned && _attempts < maxAttempts;

    /// <summary>
    ///     Whether the attempts for the current automatic target are used up.
    /// </summary>
    public bool GaveUp => _owned && _attempts >= maxAttempts;

    public int Attempts => _attempts;

    public int MaxAttempts => maxAttempts;
}
