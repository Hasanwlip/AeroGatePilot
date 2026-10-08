using AeroGatePilot.Core;

namespace AeroGatePilot.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    [InlineData(1440, "1 d")]
    [InlineData(4320, "3 d")]
    [InlineData(1500, "25 h")]
    public void Minutes_UsesCompactUnits(int minutes, string expected) =>
        Assert.Equal(expected, Formatting.Minutes(minutes));

    [Fact]
    public void Bytes_ScalesToLargestUnit() =>
        Assert.Equal("2 GB", Formatting.Bytes(2L * 1024 * 1024 * 1024));
}
