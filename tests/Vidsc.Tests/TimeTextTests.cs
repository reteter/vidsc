using Vidsc.Core.Time;

using Xunit;

namespace Vidsc.Tests;

public sealed class TimeTextTests
{
    [Theory]
    [InlineData(0L, "00:00:00.000")]
    [InlineData(1_500_000L, "00:00:00.150")]
    [InlineData(62_500_000L, "00:00:06.250")]
    [InlineData(3_723_456_789L, "00:06:12.345")]
    public void Format_ProducesFixedWidthTimecode(long ticks, string expected)
    {
        Assert.Equal(expected, TimeText.Format(ticks));
    }

    [Fact]
    public void Format_HandlesHoursAndNegatives()
    {
        Assert.Equal("01:02:03.000", TimeText.Format(TimeSpan.FromHours(1).Ticks + TimeSpan.FromMinutes(2).Ticks + TimeSpan.FromSeconds(3).Ticks + 4_000));
        Assert.Equal("-00:00:01.000", TimeText.Format(-TimeSpan.TicksPerSecond));
        Assert.Equal("00:00:00.000", TimeText.Format(0));
    }

    [Theory]
    [InlineData("90", 900_000_000L)]
    [InlineData("1.5", 15_000_000L)]
    [InlineData("1:02", 620_000_000L)]
    [InlineData("1:02.5", 625_000_000L)]
    [InlineData("00:01:02.345", 623_450_000L)]
    [InlineData("0:00:00.0000001", 1L)]
    [InlineData("-2.25", -22_500_000L)]
    public void TryParseTicks_AcceptsTheDocumentedForms(string text, long expected)
    {
        Assert.True(TimeText.TryParseTicks(text, out long ticks));
        Assert.Equal(expected, ticks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("1:-2")]
    [InlineData("1:2.5:3")]
    public void TryParseTicks_RejectsMalformedInput(string text)
    {
        Assert.False(TimeText.TryParseTicks(text, out long ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TryParseTicks_RoundsSubTickInputAwayFromZero()
    {
        // 0.00000005 s is half a tick; the parser rounds explicitly instead of truncating.
        Assert.True(TimeText.TryParseTicks("0.00000005", out long ticks));
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void FormatAndParse_RoundTripForMillisecondAlignedValues()
    {
        long original = TimeSpan.FromSeconds(3697.152).Ticks;

        Assert.True(TimeText.TryParseTicks(TimeText.Format(original), out long parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Format_IsMillisecondPreciseByContract()
    {
        // A sub-millisecond value is displayed truncated to milliseconds; the parser is exact, the
        // formatter is deliberately not. This documents the precision limit of the UI readout.
        long original = TimeSpan.FromSeconds(3697.151792).Ticks;

        Assert.True(TimeText.TryParseTicks(TimeText.Format(original), out long parsed));
        Assert.Equal(36_971_510_000L, parsed);
        Assert.NotEqual(original, parsed);
    }

    [Fact]
    public void FormatDuration_IsCompact()
    {
        Assert.Equal("05.000s", TimeText.FormatDuration(TimeSpan.FromSeconds(5).Ticks));
        Assert.Equal("01m 05.000s", TimeText.FormatDuration(TimeSpan.FromSeconds(65).Ticks));
        Assert.Equal("1h 01m 05.000s", TimeText.FormatDuration(TimeSpan.FromSeconds(3665).Ticks));
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335:00:00")]
    [InlineData("9999999999999999999:59:59.9999999")]
    public void TryParseTicks_RejectsOversizedNumericInputWithoutThrowing(string text)
    {
        // decimal.MaxValue is representable by decimal, so the multiplication used to overflow and
        // escape the Try* contract.
        Assert.False(TimeText.TryParseTicks(text, out long ticks));
        Assert.Equal(0, ticks);
    }
}
