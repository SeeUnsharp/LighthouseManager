using LighthouseManager.Core;

namespace LighthouseManager.Tests.Core;

public class MacAddressTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa:bb:cc:dd:ee:01")]
    [InlineData("AA-BB-CC-DD-EE-01")]
    [InlineData("AABBCCDDEE01")]
    [InlineData("  AA:BB:CC:DD:EE:01  ")]
    public void TryParse_accepts_common_formats(string text)
    {
        Assert.True(MacAddress.TryParse(text, out var address));
        Assert.Equal(0xAABBCCDDEE01UL, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:01:02")]
    [InlineData("GG:BB:CC:DD:EE:01")]
    [InlineData("0xAABBCCDDEE")]
    // Separators in the wrong places or mixed: typos must not become valid addresses
    [InlineData("A:ABB:CC-DDEE:FF")]
    [InlineData("AABBCC:DDEEFF")]
    [InlineData("AA:BB-CC:DD-EE:01")]
    [InlineData("AA:BB:CC:DD:EE:01:")]
    [InlineData(":AA:BB:CC:DD:EE:01")]
    public void TryParse_rejects_invalid_input(string? text)
    {
        Assert.False(MacAddress.TryParse(text, out _));
    }

    [Fact]
    public void Format_pads_leading_zeros()
    {
        // The pre-migration code dropped leading zeros ("A:..." instead of "0A:...")
        Assert.Equal("0A:0B:0C:0D:0E:0F", MacAddress.Format(0x0A0B0C0D0E0FUL));
        Assert.Equal("00:00:00:00:00:01", MacAddress.Format(1));
    }

    [Fact]
    public void Format_and_TryParse_round_trip()
    {
        const ulong address = 0x02FF10A0B0C0UL;
        Assert.True(MacAddress.TryParse(MacAddress.Format(address), out var parsed));
        Assert.Equal(address, parsed);
    }

    [Fact]
    public void ToDeviceId_uses_the_BlueZ_format_in_the_net10_build()
    {
        // The tests reference the plain net10.0 (BlueZ) build of Core, whatever OS they run on
        Assert.Equal("0A:0B:0C:0D:0E:0F", MacAddress.ToDeviceId(0x0A0B0C0D0E0FUL));
    }

    [Fact]
    public void TryParseList_parses_all_and_removes_duplicates()
    {
        Assert.True(MacAddress.TryParseList(["AA:BB:CC:DD:EE:01", "aabbccddee02", "AA-BB-CC-DD-EE-01"],
            out var addresses, out var invalid));
        Assert.Null(invalid);
        Assert.Equal([0xAABBCCDDEE01UL, 0xAABBCCDDEE02UL], addresses);
    }

    [Fact]
    public void TryParseList_reports_the_first_invalid_address()
    {
        Assert.False(MacAddress.TryParseList(["AA:BB:CC:DD:EE:01", "bad", "worse"], out _, out var invalid));
        Assert.Equal("bad", invalid);
    }
}
