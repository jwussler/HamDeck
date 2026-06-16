using HamDeck.Helpers;
using Xunit;

namespace HamDeck.Tests;

public class BandHelperTests
{
    [Theory]
    [InlineData(1_800_000, "160m")]   // lower edge inclusive
    [InlineData(2_000_000, "160m")]   // upper edge inclusive
    [InlineData(7_200_000, "40m")]
    [InlineData(14_200_000, "20m")]
    [InlineData(28_400_000, "10m")]
    [InlineData(50_125_000, "6m")]
    public void GetBand_ReturnsBand_WithinAllocation(long hz, string expected)
        => Assert.Equal(expected, BandHelper.GetBand(hz));

    [Theory]
    [InlineData(0)]
    [InlineData(2_500_000)]    // between 160m and 80m
    [InlineData(30_000_000)]   // between 10m and 6m
    [InlineData(146_000_000)]  // 2m — not in HF table
    public void GetBand_ReturnsEmpty_OutOfBand(long hz)
        => Assert.Equal("", BandHelper.GetBand(hz));

    [Theory]
    [InlineData(3_700_000, "LSB")]    // 80m phone -> LSB (below 10 MHz)
    [InlineData(7_200_000, "LSB")]    // 40m -> LSB
    [InlineData(5_350_000, "USB")]    // 60m -> USB by band plan
    [InlineData(10_125_000, "CW")]    // 30m -> CW/digital only
    [InlineData(14_200_000, "USB")]   // 20m -> USB
    [InlineData(28_400_000, "USB")]   // 10m -> USB
    public void GetModeForFrequency_MatchesBandPlan(long hz, string expected)
        => Assert.Equal(expected, BandHelper.GetModeForFrequency(hz));

    [Theory]
    [InlineData(0, "S0")]
    [InlineData(-5, "S0")]
    [InlineData(120, "S9")]      // s9 threshold
    [InlineData(255, "S9+60")]   // full scale clamps to +60
    public void RawToSUnit_KnownPoints(int raw, string expected)
        => Assert.Equal(expected, BandHelper.RawToSUnit(raw));

    [Fact]
    public void RawToSUnit_MidScale_IsBetweenS1AndS9()
    {
        var s = BandHelper.RawToSUnit(60);
        Assert.StartsWith("S", s);
        Assert.DoesNotContain("+", s);   // below S9, no plus suffix
    }
}
