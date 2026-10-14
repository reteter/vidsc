using Vidsc.Core.Time;

using Xunit;

namespace Vidsc.Tests;

public sealed class RationalTests
{
    [Fact]
    public void Create_ReducesToLowestTerms()
    {
        Rational value = Rational.Create(24000, 48000);

        Assert.Equal(1, value.Numerator);
        Assert.Equal(2, value.Denominator);
    }

    [Fact]
    public void Create_NormalizesSignToTheNumerator()
    {
        Rational value = Rational.Create(3, -6);

        Assert.Equal(-1, value.Numerator);
        Assert.Equal(2, value.Denominator);
        Assert.True(value.IsNegative);
    }

    [Fact]
    public void Create_ZeroDenominator_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rational.Create(1, 0));
    }

    [Fact]
    public void Create_Overflow_ThrowsInsteadOfWrapping()
    {
        // Negating long.MinValue reduces to a value that no longer fits in a long numerator.
        Assert.Throws<OverflowException>(() => Rational.Create(long.MinValue, -1));
    }

    [Fact]
    public void Equality_IsExactForDifferentlyReducedForms()
    {
        Rational third = Rational.Create(1, 3);
        Rational twoSixths = Rational.Create(2, 6);

        Assert.Equal(third, twoSixths);
        Assert.Equal(third.GetHashCode(), twoSixths.GetHashCode());
    }

    [Fact]
    public void Comparison_IsExactWhereDoubleWouldCollapse()
    {
        // 1/3 and 0.3333333333333333 are equal as doubles but must never compare equal here.
        Rational third = Rational.Create(1, 3);
        Rational decimalApproximation = Rational.Create(3333333333333333, 10_000_000_000_000_000);

        Assert.NotEqual(third, decimalApproximation);
        Assert.True(third > decimalApproximation);
    }

    [Fact]
    public void Comparison_CrossMultipliesWithoutOverflow()
    {
        Rational huge = Rational.Create(long.MaxValue - 1, 1);
        Rational small = Rational.Create(1, long.MaxValue - 1);

        Assert.True(huge > small);
        Assert.True(small < huge);
    }

    [Theory]
    [InlineData(7, 2, 3, 4)]
    [InlineData(-7, 2, -4, -3)]
    [InlineData(-1, 2, -1, 0)]
    [InlineData(0, 5, 0, 0)]
    public void FloorAndCeiling_HandleNegativeValues(long numerator, long denominator, long expectedFloor, long expectedCeiling)
    {
        Rational value = Rational.Create(numerator, denominator);

        Assert.Equal(expectedFloor, value.Floor());
        Assert.Equal(expectedCeiling, value.Ceiling());
    }

    [Fact]
    public void Multiply_Overflow_Throws()
    {
        Rational big = Rational.Create(long.MaxValue / 2, 1);
        Rational other = Rational.Create(long.MaxValue / 2, 1);

        Assert.Throws<OverflowException>(() => big.Multiply(other));
    }

    [Fact]
    public void Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => Rational.One.Divide(Rational.Zero));
    }

    [Fact]
    public void Arithmetic_KeepsExactFractionalResults()
    {
        // 23.976 fps expressed as a rational frame duration.
        Rational frameDuration = Rational.One.Divide(Rational.Create(24000, 1001));

        Assert.Equal(1001, frameDuration.Numerator);
        Assert.Equal(24000, frameDuration.Denominator);

        Rational twentyFourFrames = frameDuration.Multiply(Rational.Create(24, 1));

        Assert.Equal(1001, twentyFourFrames.Numerator);
        Assert.Equal(1000, twentyFourFrames.Denominator);
        Assert.Equal(1.001, twentyFourFrames.ToDouble(), 12);
    }

    [Fact]
    public void ToString_ShowsReducedForm()
    {
        Assert.Equal("1001/24000", Rational.One.Divide(Rational.Create(24000, 1001)).ToString());
        Assert.Equal("5", Rational.Create(10, 2).ToString());
    }

    [Fact]
    public void DefaultStruct_IsZeroAndNeverExposesAZeroDenominator()
    {
        // The chosen contract: default(Rational) is Zero, not an invalid 0/0.
        Rational value = default;

        Assert.True(value.IsZero);
        Assert.Equal(0, value.Numerator);
        Assert.Equal(1, value.Denominator);
        Assert.Equal(Rational.Zero, value);
        Assert.Equal(Rational.Zero.GetHashCode(), value.GetHashCode());
        Assert.Equal(0, value.CompareTo(Rational.Zero));
        Assert.Equal(Rational.Create(1, 2), value.Add(Rational.Create(1, 2)));
        Assert.Equal("0", value.ToString());
    }

    [Fact]
    public void Abs_AndNegate_ThrowForLongMinValueInsteadOfStayingNegative()
    {
        Rational minValue = Rational.Create(long.MinValue, 1);

        Assert.True(minValue.IsNegative);
        Assert.Throws<OverflowException>(() => minValue.Abs());
        Assert.Throws<OverflowException>(() => minValue.Negate());

        // Representable negatives still negate exactly.
        Assert.Equal(Rational.Create(-5, 2), Rational.Create(5, 2).Negate());
        Assert.Equal(Rational.Create(5, 2), Rational.Create(-5, 2).Abs());
        Assert.Equal(Rational.Zero, Rational.Zero.Negate());
    }
}
