using LighthouseManager.Tray.ViewModels;

namespace LighthouseManager.Tests.Tray;

public class DimmingSliderTests
{
    [Fact]
    public void Positions_are_off_kde_default_then_half_seconds_up_to_ten()
    {
        Assert.Equal((false, null), DimmingSlider.ToRequest(0));
        Assert.Equal((true, 0.0), DimmingSlider.ToRequest(1));
        Assert.Equal((true, 0.5), DimmingSlider.ToRequest(2));
        Assert.Equal((true, 10.0), DimmingSlider.ToRequest(DimmingSlider.Maximum));
        Assert.Equal(21, DimmingSlider.Maximum);
    }

    [Fact]
    public void Status_maps_back_to_the_same_position()
    {
        for (var position = 0; position <= DimmingSlider.Maximum; position++)
        {
            var (enabled, fade) = DimmingSlider.ToRequest(position);
            // Switching off keeps the service's fade; any fade maps back to off then
            Assert.Equal(position, DimmingSlider.FromStatus(enabled, fade ?? 1.5));
        }
    }

    [Fact]
    public void Off_ignores_the_fade_and_odd_values_snap_to_the_nearest_step()
    {
        Assert.Equal(0, DimmingSlider.FromStatus(false, 3));
        // E.g. set by hand in the settings file
        Assert.Equal(4, DimmingSlider.FromStatus(true, 1.4));
        Assert.Equal(DimmingSlider.Maximum, DimmingSlider.FromStatus(true, 99));
    }

    [Fact]
    public void Labels()
    {
        Assert.Equal("Off", DimmingSlider.Text(0));
        Assert.Equal("KDE default", DimmingSlider.Text(1));
        Assert.Equal($"{1.5:0.#} s", DimmingSlider.Text(4));
        Assert.Equal("10 s", DimmingSlider.Text(DimmingSlider.Maximum));
    }
}
