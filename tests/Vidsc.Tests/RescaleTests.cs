using Vidsc.Core.Time;

using Xunit;

namespace Vidsc.Tests;

public sealed class RescaleTests
{
    private static readonly Rational Fps24000Over1001TimeBase = Rational.Create(1, 24000);

    [Fact]
    public void Rescale_FractionalFrameRateIntoFFmpegMillisecondBase_IsExactWhenRepresentable()
    {
        // 24000/1001 fps: the PTS time base is 1/24000, so 24024 PTS units are exactly 1.001 s.
        long milliseconds = TimeRescale.Rescale(24024, Fps24000Over1001TimeBase, TimeBases.Milliseconds);

        Assert.Equal(1001, milliseconds);

        // 24000 units are exactly one second.
        Assert.Equal(1000, TimeRescale.Rescale(24000, Fps24000Over1001TimeBase, TimeBases.Milliseconds));
    }

    [Theory]
    [InlineData(RescaleRounding.NearestAwayFromZero, 417083L)]
    [InlineData(RescaleRounding.TowardZero, 417083L)]
    [InlineData(RescaleRounding.Floor, 417083L)]
    [InlineData(RescaleRounding.Ceiling, 417084L)]
    public void Rescale_AppliesTheRequestedRoundingPolicy(RescaleRounding rounding, long expected)
    {
        // 1001 PTS units at 1/24000 s = 1001/24000 s = 0.04170833... s = 417083.333... ticks.
        long ticks = TimeRescale.Rescale(1001, Fps24000Over1001TimeBase, TimeBases.Ticks, rounding);

        Assert.Equal(expected, ticks);
    }

    [Fact]
    public void Rescale_NegativeValues_RoundAwayFromZero()
    {
        long ticks = TimeRescale.Rescale(-1001, Fps24000Over1001TimeBase, TimeBases.Ticks);

        Assert.Equal(-417083, ticks);
    }

    [Fact]
    public void Rescale_ErrorStaysWithinOneDestinationTick()
    {
        const int frameCount = 5000;
        Rational ticksBase = TimeBases.Ticks;

        for (int index = 0; index < frameCount; index++)
        {
            long ticks = TimeRescale.Rescale(index, Fps24000Over1001TimeBase, ticksBase);
            long backToPts = TimeRescale.Rescale(ticks, ticksBase, Fps24000Over1001TimeBase);

            long errorInPts = Math.Abs(backToPts - index);

            // The documented bound: one destination unit (one PTS unit here) of error, never more.
            Assert.True(errorInPts <= 1, $"frame {index}: error {errorInPts} PTS units exceeds one tick of the destination base");
        }
    }

    [Fact]
    public void Rescale_StandardFrameRatesLandOnExactMultiples()
    {
        // 24/1 fps in a 1/24000 base: 24000 PTS units are exactly one second.
        Assert.Equal(10_000_000, TimeRescale.Rescale(24000, Fps24000Over1001TimeBase, TimeBases.Ticks));

        // 25/1 fps in a 1/25000 base: one PTS unit is exactly one millisecond.
        Assert.Equal(1, TimeRescale.Rescale(25, Rational.Create(1, 25000), TimeBases.Milliseconds));
    }

    [Fact]
    public void Rescale_OverflowThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => TimeRescale.Rescale(long.MaxValue, TimeBases.Seconds, TimeBases.Ticks));
    }

    [Fact]
    public void Rescale_FullRangeTripleProduct_DoesNotWrap()
    {
        // The review's repro: value * fromNumerator * toDenominator overflows 128 bits for full-range
        // bases, which previously wrapped to 0 instead of returning the identity for equal bases.
        Rational baseA = Rational.Create(4_611_686_018_427_387_904, 4_611_686_018_427_387_903);

        Assert.Equal(16, TimeRescale.Rescale(16, baseA, baseA));
        Assert.Equal(1, TimeRescale.Rescale(1, baseA, baseA));
        Assert.Equal(-16, TimeRescale.Rescale(-16, baseA, baseA));
    }

    [Fact]
    public void Rescale_OutOfRangeResult_ThrowsInsteadOfWrapping()
    {
        // An exact (non-wrapping) product whose rounded result cannot fit in long must throw.
        Assert.Throws<OverflowException>(() =>
            TimeRescale.Rescale(long.MaxValue, TimeBases.Seconds, Rational.Create(1, long.MaxValue)));
    }

    [Fact]
    public void RationalTime_UnitArithmeticChecksOverflow()
    {
        var max = new RationalTime(long.MaxValue, TimeBases.Ticks);
        var min = new RationalTime(long.MinValue, TimeBases.Ticks);

        Assert.Throws<OverflowException>(() => max.AddUnits(1));
        Assert.Throws<OverflowException>(() => min.SubtractUnits(1));
        Assert.Throws<OverflowException>(() => min.Subtract(new RationalTime(1, TimeBases.Ticks)));

        Assert.Equal(long.MaxValue, max.AddUnits(0).Value);
    }

    [Fact]
    public void Rescale_NonPositiveDestinationBase_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeRescale.Rescale(1, TimeBases.Ticks, Rational.Zero));
    }

    [Fact]
    public void MaximumRescaleError_IsExactlyOneDestinationUnit()
    {
        Assert.Equal(TimeBases.Ticks, TimeRescale.MaximumRescaleError(TimeBases.Ticks));
        Assert.Equal(TimeBases.Milliseconds, TimeRescale.MaximumRescaleError(TimeBases.Milliseconds));
    }

    [Fact]
    public void RationalTime_ComparisonIsExactAcrossBases()
    {
        // 1.001 s expressed as 24024 units of a 1/24000 s base versus 1001 ms.
        var fromPts = new RationalTime(24024, Fps24000Over1001TimeBase);
        var fromMilliseconds = new RationalTime(1001, TimeBases.Milliseconds);

        Assert.Equal(0, fromPts.CompareTo(fromMilliseconds));
        Assert.Equal(fromPts.AsSeconds(), fromMilliseconds.AsSeconds());
    }

    [Fact]
    public void RationalTime_ToBaseIsExplicitAndDocumented()
    {
        var time = new RationalTime(24024, Fps24000Over1001TimeBase);

        RationalTime rescaled = time.ToBase(TimeBases.Milliseconds);

        Assert.Equal(1001, rescaled.Value);
        Assert.Equal(TimeBases.Milliseconds, rescaled.TimeBase);
    }

    [Fact]
    public void RationalTime_SubtractRequiresMatchingBases()
    {
        var a = new RationalTime(10, TimeBases.Ticks);
        var b = new RationalTime(10, TimeBases.Milliseconds);

        Assert.Throws<InvalidOperationException>(() => a.Subtract(b));
        Assert.Equal(0, a.Subtract(new RationalTime(10, TimeBases.Ticks)).Value);
    }

    [Fact]
    public void MediaInterval_IsHalfOpen()
    {
        var start = new RationalTime(1000, TimeBases.Ticks);
        var end = new RationalTime(2000, TimeBases.Ticks);
        var interval = new MediaInterval(start, end);

        Assert.True(interval.Contains(start));
        Assert.True(interval.Contains(new RationalTime(1999, TimeBases.Ticks)));
        Assert.False(interval.Contains(end));
        Assert.Equal(Rational.Create(1, 10_000), interval.Duration);
        Assert.False(interval.IsEmpty);
    }

    [Fact]
    public void MediaInterval_IntersectsPresentation_UsesPresentationEnd()
    {
        var interval = new MediaInterval(new RationalTime(100, TimeBases.Ticks), new RationalTime(200, TimeBases.Ticks));

        // A frame that starts exactly at the interval end does not intersect it.
        Assert.False(interval.IntersectsPresentation(new RationalTime(200, TimeBases.Ticks), new RationalTime(300, TimeBases.Ticks)));

        // A frame whose presentation end equals the interval start does not intersect it either.
        Assert.False(interval.IntersectsPresentation(new RationalTime(50, TimeBases.Ticks), new RationalTime(100, TimeBases.Ticks)));

        // A frame straddling the start does intersect it (Out would include its presentation end).
        Assert.True(interval.IntersectsPresentation(new RationalTime(50, TimeBases.Ticks), new RationalTime(150, TimeBases.Ticks)));
    }

    [Fact]
    public void MediaInterval_EmptyIntervalHasNoDuration()
    {
        var interval = new MediaInterval(new RationalTime(500, TimeBases.Ticks), new RationalTime(500, TimeBases.Ticks));

        Assert.True(interval.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => _ = interval.Duration);
    }

    [Fact]
    public void MediaInterval_EmptyIntervalNeverIntersects()
    {
        var empty = new MediaInterval(new RationalTime(5, TimeBases.Ticks), new RationalTime(5, TimeBases.Ticks));

        Assert.True(empty.IsEmpty);
        Assert.False(empty.IntersectsPresentation(new RationalTime(4, TimeBases.Ticks), new RationalTime(6, TimeBases.Ticks)));
        Assert.False(empty.Contains(new RationalTime(5, TimeBases.Ticks)));
    }

    [Fact]
    public void MediaInterval_ReversedIntervalIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new MediaInterval(
            new RationalTime(6, TimeBases.Ticks),
            new RationalTime(5, TimeBases.Ticks)));
    }
}
