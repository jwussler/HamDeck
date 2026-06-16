using HamDeck.Helpers;
using Xunit;

namespace HamDeck.Tests;

public class FrequencyHelperTests
{
    [Theory]
    [InlineData("14.200", 14_200_000)]   // MHz with decimal
    [InlineData("7.125", 7_125_000)]
    [InlineData("3.5", 3_500_000)]
    [InlineData("14200", 14_200_000)]    // bare kHz (100..99999)
    [InlineData("14200000", 14_200_000)] // bare Hz (>=100000)
    [InlineData("14", 14_000_000)]       // bare MHz (<100)
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void Parse_InterpretsCommonForms(string input, long expectedHz)
        => Assert.Equal(expectedHz, FrequencyHelper.Parse(input));

    [Fact]
    public void Parse_StripsCommas()
        => Assert.Equal(14_200_000, FrequencyHelper.Parse("14,200,000"));

    [Theory]
    [InlineData(14_200_000, "14.200 MHz")]
    public void FormatMHz_Works(long hz, string expected)
        => Assert.Equal(expected, FrequencyHelper.FormatMHz(hz));
}
