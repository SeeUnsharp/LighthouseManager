using LighthouseManager.Tray;

namespace LighthouseManager.Tests.Tray;

public class TrayToolTipTests
{
    [Fact]
    public void Lists_every_station_when_it_fits()
    {
        var text = TrayToolTip.Build("No trigger process running", [("LHB-1A2B3C4D", "On"), ("LHB-5E6F7A8B", "Sleeping")]);

        Assert.Equal("LighthouseManager\nNo trigger process running\nLHB-1A2B3C4D: On\nLHB-5E6F7A8B: Sleeping", text);
    }

    [Fact]
    public void Summarizes_when_the_list_is_too_long()
    {
        var stations = Enumerable.Range(0, 8)
            .Select(i => ($"LHB-0000000{i}", i < 3 ? "On" : i < 7 ? "Sleeping" : "Starting…"))
            .ToList();

        var text = TrayToolTip.Build("In use by vrserver", stations);

        Assert.Equal("LighthouseManager\nIn use by vrserver\n8 base stations: 3 On, 4 Sleeping, 1 Starting…", text);
        Assert.True(text.Length <= TrayToolTip.MaxLength);
    }

    [Fact]
    public void Never_exceeds_the_Windows_limit()
    {
        var states = new[] { "On", "Sleeping", "Starting…", "Standby", "Unknown", "Working…", "Not reachable" };
        var stations = Enumerable.Range(0, 16).Select(i => ($"LHB-{i:X8}", states[i % states.Length])).ToList();

        var text = TrayToolTip.Build("Service not reachable (127.0.0.1:47800)", stations);

        Assert.True(text.Length <= TrayToolTip.MaxLength, $"{text.Length} chars");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Mentions_an_available_update_below_the_status()
    {
        var text = TrayToolTip.Build("No trigger process running", [("LHB-1A2B3C4D", "On")], "2.0.1");

        Assert.Equal("LighthouseManager\nNo trigger process running\nUpdate available: 2.0.1\nLHB-1A2B3C4D: On", text);
    }

    [Fact]
    public void Shows_only_the_status_without_stations()
    {
        Assert.Equal("LighthouseManager\nService not reachable (127.0.0.1:47800)",
            TrayToolTip.Build("Service not reachable (127.0.0.1:47800)", []));
    }
}
