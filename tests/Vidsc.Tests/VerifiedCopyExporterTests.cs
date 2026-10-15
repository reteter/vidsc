using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class VerifiedCopyExporterTests
{
    [Theory]
    [InlineData(1, 1, "1.000000")]
    [InlineData(1001, 1000, "1.001000")]
    [InlineData(0, 1, "0.000000")]
    [InlineData(1234567, 1000000, "1.234567")]
    public void SeekUsesExactInvariantMicroseconds(long value, long denominator, string expected)
        => Assert.Equal(expected, VerifiedCopyExporter.FormatExactSeek(new(value, Rational.Create(1, denominator))));

    [Theory]
    [InlineData(1, 3)]
    [InlineData(-1, 1)]
    public void UnprovedSeekRoundingIsRejected(long value, long denominator)
        => Assert.Throws<InvalidOperationException>(() =>
            VerifiedCopyExporter.FormatExactSeek(new(value, Rational.Create(1, denominator))));
}
