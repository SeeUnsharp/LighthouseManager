using LighthouseManager.Core;
using LighthouseManagerService;

namespace LighthouseManager.Tests.Service;

public class BootWatchTests
{
    private const ulong Station = 0xAABBCCDDEE01UL;
    private readonly BootWatch _watch = new();
    private readonly DateTime _start = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Normal_wake_is_followed_until_it_reports_awake()
    {
        _watch.Woken(Station, _start);

        // 0x08 right after the wake, then 0x09 / 0x0B
        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(Station, StationState.Booting, _start.AddSeconds(10)));
        Assert.Equal(BootWatch.Verdict.Settled, _watch.Observe(Station, StationState.Awake, _start.AddSeconds(25)));
    }

    [Fact]
    public void Station_that_keeps_booting_is_reported_as_stuck()
    {
        // Observed: a station staying at raw 0x01 for hours after a wake
        _watch.Woken(Station, _start);

        Assert.Equal(BootWatch.Verdict.CheckAgain,
            _watch.Observe(Station, StationState.Booting, _start + BootWatch.StuckAfter - TimeSpan.FromSeconds(1)));
        Assert.Equal(BootWatch.Verdict.Stuck, _watch.Observe(Station, StationState.Booting, _start + BootWatch.StuckAfter));
        Assert.Equal(BootWatch.Verdict.Stuck, _watch.Observe(Station, StationState.Booting, _start.AddHours(3)));
    }

    [Fact]
    public void Stuck_station_recovers_when_it_finally_reports_awake()
    {
        _watch.Woken(Station, _start);
        Assert.Equal(BootWatch.Verdict.Stuck, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(5)));

        Assert.Equal(BootWatch.Verdict.Settled, _watch.Observe(Station, StationState.Awake, _start.AddMinutes(6)));
        // A later boot starts a new timer
        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(7)));
    }

    [Fact]
    public void Booting_seen_without_a_wake_command_starts_the_timer_at_the_first_read()
    {
        // E.g. woken by another app or after the service restarted
        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(Station, StationState.Booting, _start));
        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(1)));
        Assert.Equal(BootWatch.Verdict.Stuck, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(2)));
    }

    [Fact]
    public void Sleep_or_removal_resets_the_timer()
    {
        _watch.Woken(Station, _start);
        _watch.Forget(Station);

        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(10)));
    }

    [Fact]
    public void Stations_are_tracked_independently()
    {
        const ulong other = 0xAABBCCDDEE02UL;
        _watch.Woken(Station, _start);
        _watch.Woken(other, _start.AddMinutes(2));

        Assert.Equal(BootWatch.Verdict.Stuck, _watch.Observe(Station, StationState.Booting, _start.AddMinutes(3)));
        Assert.Equal(BootWatch.Verdict.CheckAgain, _watch.Observe(other, StationState.Booting, _start.AddMinutes(3)));
    }
}
