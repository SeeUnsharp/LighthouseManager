using LighthouseManager.Core;

namespace LighthouseManager.Tests.Core;

public class LighthouseTests
{
    // Values observed on real Lighthouse 2.0 base stations
    [Theory]
    [InlineData(0x00, StationState.Sleeping)]
    [InlineData(0x02, StationState.Standby)]
    [InlineData(0x01, StationState.Booting)]
    [InlineData(0x08, StationState.Booting)]
    [InlineData(0x09, StationState.Awake)]
    [InlineData(0x0B, StationState.Awake)]
    [InlineData(0x42, StationState.Unknown)]
    [InlineData(0xFF, StationState.Unknown)]
    public void ToStationState_maps_raw_values(byte raw, StationState expected)
    {
        Assert.Equal(expected, Lighthouse.ToStationState(raw));
    }

    [Theory]
    [InlineData(Powerstate.Sleep, 0)]
    [InlineData(Powerstate.Wake, 1)]
    [InlineData(Powerstate.Standby, 2)]
    public void WriteValue_matches_the_GATT_protocol(Powerstate state, byte expected)
    {
        Assert.Equal(expected, Lighthouse.WriteValue(state));
    }

    [Theory]
    [InlineData(0x09, Powerstate.Wake, true)]
    [InlineData(0x0B, Powerstate.Wake, true)]
    // Booting is not "already awake": the write is repeated, which is harmless
    [InlineData(0x08, Powerstate.Wake, false)]
    [InlineData(0x00, Powerstate.Wake, false)]
    [InlineData(0x00, Powerstate.Sleep, true)]
    [InlineData(0x0B, Powerstate.Sleep, false)]
    [InlineData(0x02, Powerstate.Standby, true)]
    public void IsInState_decides_whether_a_write_can_be_skipped(byte raw, Powerstate state, bool expected)
    {
        Assert.Equal(expected, Lighthouse.IsInState(raw, state));
    }
}
