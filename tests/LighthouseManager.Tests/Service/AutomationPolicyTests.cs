using LighthouseManager.Core;
using LighthouseManagerService;

namespace LighthouseManager.Tests.Service;

public class AutomationPolicyTests
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private readonly AutomationPolicy _policy = new(3, RetryDelay);
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private bool _automatic = true;
    private int _override;

    private Powerstate? Tick(bool triggerRunning, bool lastJobFailed = false)
    {
        _now += TimeSpan.FromSeconds(1);
        return _policy.Next(triggerRunning, _automatic, _override, lastJobFailed, _now);
    }

    [Fact]
    public void Does_nothing_until_a_trigger_ran()
    {
        Assert.Null(Tick(false));
        Assert.Null(Tick(false));
    }

    [Fact]
    public void Wakes_on_trigger_start_and_sleeps_on_trigger_exit_once()
    {
        Assert.Equal(Powerstate.Wake, Tick(true));
        Assert.Null(Tick(true));
        Assert.Equal(Powerstate.Sleep, Tick(false));
        Assert.Null(Tick(false));
        Assert.Equal(Powerstate.Wake, Tick(true));
    }

    [Fact]
    public void Manual_command_wins_until_the_trigger_changes()
    {
        Assert.Equal(Powerstate.Wake, Tick(true));

        // User puts the stations to sleep while the trigger still runs
        _override++;
        Assert.Null(Tick(true));
        Assert.Null(Tick(true));

        // Next trigger change is handled again
        Assert.Equal(Powerstate.Sleep, Tick(false));
    }

    [Fact]
    public void Manual_command_cancelling_an_automatic_job_is_not_retried()
    {
        // Regression: a manual sleep cancelled the running automatic wake, the cancelled job counted as failed and
        // the watcher "retried" the wake, undoing the manual command.
        Assert.Equal(Powerstate.Wake, Tick(true));
        _override++;
        _policy.ReportFailure(_now);
        Assert.Null(Tick(true, lastJobFailed: true));

        _now += RetryDelay * 2;
        Assert.Null(Tick(true, lastJobFailed: true));
        Assert.False(_policy.WillRetry);
    }

    [Fact]
    public void Disabling_automatic_mode_stops_retries_and_reenabling_waits_for_the_next_change()
    {
        Assert.Equal(Powerstate.Wake, Tick(true));

        _automatic = false;
        _policy.ReportFailure(_now);
        Assert.Null(Tick(true, lastJobFailed: true));
        Assert.Null(Tick(false));

        // Back on while no trigger runs: nothing happens (the stations may have been woken manually)
        _automatic = true;
        _now += RetryDelay * 2;
        Assert.Null(Tick(false, lastJobFailed: true));
        Assert.Equal(Powerstate.Wake, Tick(true));
    }

    [Fact]
    public void Failed_job_is_retried_after_the_delay_up_to_the_maximum()
    {
        Assert.Equal(Powerstate.Wake, Tick(true));

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            _policy.ReportFailure(_now);
            Assert.True(_policy.WillRetry);
            Assert.Null(Tick(true, lastJobFailed: true));

            _now += RetryDelay;
            Assert.Equal(Powerstate.Wake, Tick(true, lastJobFailed: true));
            Assert.Equal(attempt, _policy.Attempts);
        }

        _policy.ReportFailure(_now);
        Assert.True(_policy.GaveUp);
        _now += RetryDelay;
        Assert.Null(Tick(true, lastJobFailed: true));

        // A trigger change starts over
        Assert.Equal(Powerstate.Sleep, Tick(false));
        Assert.Equal(1, _policy.Attempts);
    }

    [Fact]
    public void Successful_job_is_not_repeated()
    {
        Assert.Equal(Powerstate.Wake, Tick(true));
        _now += RetryDelay * 2;
        Assert.Null(Tick(true, lastJobFailed: false));
    }
}
