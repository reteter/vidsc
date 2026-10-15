using Vidsc.Core.Time;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Media.Tooling;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class VerifiedCopyExporterTests
{
    [Fact]
    public async Task InvalidSeekIsRefusedBeforeSourceOrTransactionIo()
    {
        var tb = Rational.Create(1, 3);
        var request = new FiniteCopyRequest(new("unopened", 1, 0, tb, 0), "h264", 0, 1, 1, 2);
        var qualified = new QualifiedCopyRange(request, "hash", "config", "manifest", "build", 0);
        var interval = new ResolvedFrameInterval(new(new(1, tb), new(1, tb)), new(new(2, tb), new(2, tb)));
        var proposal = new CopyBoundaryProposal(interval, qualified.ToObservation());
        var exporter = new VerifiedCopyExporter(new("unused", "unused"), new FfmpegCli("unused", "unused"));
        // Neither path exists: exact-seek rejection must precede any file access.
        string absent = Path.Combine(Path.GetTempPath(), "vidsc-unused-" + Guid.NewGuid().ToString("N"));
        var result = await exporter.ExportVideoOnlyAsync(Path.Combine(absent, "source"), qualified,
            proposal, Path.Combine(absent, "output"), Path.Combine(absent, "journals"), default);
        Assert.False(result.Success);
        Assert.Contains("exactly representable in microseconds", result.Error);
        Assert.Null(result.JournalPath);
        Assert.False(Directory.Exists(absent));
    }

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
