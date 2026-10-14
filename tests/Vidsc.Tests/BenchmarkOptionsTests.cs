using Vidsc.App.Services;
using Xunit;
namespace Vidsc.Tests;

public sealed class BenchmarkOptionsTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("seek")]
    [InlineData("playback")]
    [InlineData("lifecycle")]
    public void ParsesBenchmark(string scenario)
    {
        var options = CommandLineOptions.Parse(["--benchmark", scenario, "--media", "clip.mp4", "--report", "report.json"]);
        Assert.Equal(scenario, options.Benchmark);
        Assert.Equal(900, options.BenchmarkDurationSeconds);
        Assert.Equal(3, options.BenchmarkOpenCount);
    }

    [Theory]
    [InlineData("--benchmark", "unknown")]
    [InlineData("--duration-seconds", "0")]
    [InlineData("--duration-seconds", "-1")]
    [InlineData("--open-count", "bad")]
    [InlineData("--media", "--report")]
    public void RejectsInvalidValues(string option, string value) =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse([option, value]));

    [Fact]
    public void RequiresBenchmarkInputs() =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--benchmark", "seek"]));

    [Fact]
    public void RejectsCanonicalReportAliasToAnyMedia()
    {
        string source = Path.Combine(Path.GetTempPath(), "vidsc-alias", "clip.mp4");
        string alias = Path.Combine(Path.GetTempPath(), "vidsc-alias", "nested", "..", "CLIP.mp4");
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(
            ["--benchmark", "open", "--media", "other.mp4", "--media", source, "--report", alias]));
    }

    [Fact]
    public void RejectsExistingReportWithoutChangingContents()
    {
        string report = Path.Combine(Path.GetTempPath(), "vidsc-benchmark-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(report, "preserve existing bytes");
            Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(
                ["--benchmark", "open", "--media", "clip.mp4", "--report", report]));
            Assert.Equal("preserve existing bytes", File.ReadAllText(report));
        }
        finally { File.Delete(report); }
    }

    [Fact]
    public void PreservesSmokeAndConstructor()
    {
        Assert.True(CommandLineOptions.Parse(["--smoke-test", "--media", "clip.mp4"]).SmokeTest);
        Assert.Null(new CommandLineOptions(false, [], null, null, false).Benchmark);
    }
}
