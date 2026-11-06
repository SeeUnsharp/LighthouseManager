using LighthouseManagerService.Dimming;

namespace LighthouseManager.Tests.Service;

public class VrSessionsTests
{
    private const MonadoClientFlags Shown = MonadoClientFlags.SessionActive | MonadoClientFlags.SessionVisible;

    [Fact]
    public void No_runtime_or_no_clients_means_no_game()
    {
        Assert.Null(VrSessions.FindGame(null));
        Assert.Null(VrSessions.FindGame([]));
    }

    [Fact]
    public void Clients_without_a_visible_session_are_not_games()
    {
        // E.g. monitoring tools (connected, no session) or a game still loading
        Assert.Null(VrSessions.FindGame([
            new VrClient("monado-ctl", 0),
            new VrClient("Loading", MonadoClientFlags.PrimaryApp | MonadoClientFlags.SessionActive)
        ]));
    }

    [Fact]
    public void Overlays_are_not_games()
    {
        Assert.Null(VrSessions.FindGame([new VrClient("WlxOverlay-S", Shown | MonadoClientFlags.SessionOverlay)]));
        Assert.Equal("Beat Saber", VrSessions.FindGame([
            new VrClient("WlxOverlay-S", Shown | MonadoClientFlags.SessionOverlay),
            new VrClient("Beat Saber", Shown | MonadoClientFlags.PrimaryApp)
        ]));
    }

    [Fact]
    public void Primary_app_is_preferred()
    {
        Assert.Equal("B", VrSessions.FindGame([
            new VrClient("A", Shown),
            new VrClient("B", Shown | MonadoClientFlags.PrimaryApp)
        ]));
        Assert.Equal("A", VrSessions.FindGame([new VrClient("A", Shown)]));
    }
}

public class DimmingPolicyTests
{
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private readonly DateTime _start = new(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dims_when_a_game_is_shown_and_restores_after_the_delay()
    {
        var policy = new DimmingPolicy(RestoreDelay, RetryDelay);
        Assert.Null(policy.Next(true, false, _start));

        Assert.True(policy.Next(true, true, _start));
        policy.Applied(true);
        Assert.Null(policy.Next(true, true, _start.AddSeconds(1)));

        Assert.Null(policy.Next(true, false, _start.AddSeconds(2)));
        Assert.Null(policy.Next(true, false, _start.AddSeconds(11)));
        Assert.False(policy.Next(true, false, _start.AddSeconds(12)));
        policy.Applied(false);
        Assert.Null(policy.Next(true, false, _start.AddSeconds(13)));
    }

    [Fact]
    public void Switching_games_within_the_delay_keeps_the_displays_dimmed()
    {
        var policy = new DimmingPolicy(RestoreDelay, RetryDelay, dimmed: true);
        Assert.Null(policy.Next(true, false, _start));
        Assert.Null(policy.Next(true, true, _start.AddSeconds(5)));
        // The delay starts again when the next game ends
        Assert.Null(policy.Next(true, false, _start.AddSeconds(12)));
        Assert.False(policy.Next(true, false, _start.AddSeconds(22)));
    }

    [Fact]
    public void Disabled_does_nothing_but_restores_right_away_when_dimmed()
    {
        Assert.Null(new DimmingPolicy(RestoreDelay, RetryDelay).Next(false, true, _start));
        // Left dimmed by a previous run, or the feature turned off during a game
        Assert.False(new DimmingPolicy(RestoreDelay, RetryDelay, dimmed: true).Next(false, true, _start));
    }

    [Fact]
    public void Left_dimmed_by_a_previous_run_is_restored_when_no_game_is_shown()
    {
        var policy = new DimmingPolicy(RestoreDelay, RetryDelay, dimmed: true);
        Assert.Null(policy.Next(true, false, _start));
        Assert.False(policy.Next(true, false, _start + RestoreDelay));
    }

    [Fact]
    public void Failure_waits_for_the_retry_delay()
    {
        var policy = new DimmingPolicy(RestoreDelay, RetryDelay);
        Assert.True(policy.Next(true, true, _start));
        policy.Failed(_start);

        Assert.Null(policy.Next(true, true, _start.AddSeconds(29)));
        Assert.True(policy.Next(true, true, _start.AddSeconds(30)));
    }
}

public class DimmingPlanTests
{
    private static readonly DisplayBrightness Monitor = new("display0", "LC49G95T", 3000, 10000);
    private static readonly DisplayBrightness Laptop = new("display1", "Built-in", 80, 100);

    [Fact]
    public void Dim_remembers_and_dims_every_display()
    {
        var (saved, changes) = DimmingPlan.Dim([Monitor, Laptop], []);

        Assert.Equal([new DimmedDisplay("display0", "LC49G95T", 3000, 0), new DimmedDisplay("display1", "Built-in", 80, 0)],
            saved);
        Assert.Equal([("display0", 0), ("display1", 0)], changes);
    }

    [Fact]
    public void Dim_skips_displays_already_at_the_lowest_brightness()
    {
        var (saved, changes) = DimmingPlan.Dim([Monitor with { Brightness = 0 }], []);

        Assert.Empty(saved);
        Assert.Empty(changes);
    }

    [Fact]
    public void Dim_keeps_the_original_brightness_of_a_previous_run()
    {
        // A crash left display0 dimmed: its brightness now is 0, the saved 3000 must not be lost
        var previous = new DimmedDisplay("display0", "LC49G95T", 3000, 0);
        var (saved, changes) = DimmingPlan.Dim([Monitor with { Brightness = 0 }], [previous]);

        Assert.Equal([previous], saved);
        Assert.Empty(changes);
    }

    [Fact]
    public void Restore_sets_the_saved_brightness()
    {
        var saved = new[] { new DimmedDisplay("display0", "LC49G95T", 3000, 0) };

        Assert.Equal([("display0", 3000)], DimmingPlan.Restore([Monitor with { Brightness = 0 }], saved));
        // Rounded to the monitor's DDC/CI scale
        Assert.Equal([("display0", 3000)], DimmingPlan.Restore([Monitor with { Brightness = 100 }], saved));
    }

    [Fact]
    public void Restore_leaves_displays_changed_meanwhile_alone()
    {
        var saved = new[] { new DimmedDisplay("display0", "LC49G95T", 3000, 0) };

        Assert.Empty(DimmingPlan.Restore([Monitor with { Brightness = 5000 }], saved));
    }

    [Fact]
    public void Restore_skips_displays_that_are_gone_or_were_replaced()
    {
        var saved = new[] { new DimmedDisplay("display0", "LC49G95T", 3000, 0) };

        Assert.Empty(DimmingPlan.Restore([], saved));
        Assert.Empty(DimmingPlan.Restore([Monitor with { Label = "Other", Brightness = 0 }], saved));
    }

    [Fact]
    public void Interpolate_eases_in_and_out()
    {
        Assert.Equal(3000, DimmingPlan.Interpolate(3000, 0, 0));
        Assert.Equal(1500, DimmingPlan.Interpolate(3000, 0, 0.5));
        Assert.Equal(0, DimmingPlan.Interpolate(3000, 0, 1));
        // Slow at the start, out of range clamped
        Assert.True(DimmingPlan.Interpolate(0, 3000, 0.1) < 300);
        Assert.Equal(3000, DimmingPlan.Interpolate(0, 3000, 1.5));
    }

    [Fact]
    public void Store_round_trips_and_survives_a_broken_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dimmed-{Guid.NewGuid():N}.json");
        var store = new DimmedDisplayStore(file);
        try
        {
            Assert.False(store.Exists);
            Assert.Empty(store.Load());

            store.Save([new DimmedDisplay("display0", "LC49G95T", 3000, 0)]);
            Assert.True(store.Exists);
            Assert.Equal([new DimmedDisplay("display0", "LC49G95T", 3000, 0)], store.Load());

            File.WriteAllText(file, "{ broken");
            Assert.Empty(store.Load());

            store.Delete();
            Assert.False(store.Exists);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
