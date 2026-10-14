using Vidsc.App.Services;
using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationOptionsTests
{
    [Theory]
    [InlineData("--presentation-check")]
    [InlineData("--fixture-manifest")]
    [InlineData("--winapp-path")]
    [InlineData("--exhaustive-steps")]
    public void RejectsIncompletePresentationOptions(string option) =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse([option]));

    [Fact]
    public void RejectsOutputAliasToManifest()
    {
        string manifest = Path.GetTempFileName();
        try
        {
            string executable = Environment.ProcessPath!;
            Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse([
                "--presentation-check", "--media", "fixture.mp4", "--report", manifest,
                "--fixture-manifest", manifest, "--winapp-path", executable]));
        }
        finally { File.Delete(manifest); }
    }

    [Fact]
    public void ParsesCompletePresentationOptions()
    {
        string manifest = Path.GetTempFileName();
        try
        {
            var options = CommandLineOptions.Parse(["--presentation-check", "--media", "fixture.mp4",
                "--report", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
                "--fixture-manifest", manifest, "--winapp-path", Environment.ProcessPath!]);
            Assert.True(options.PresentationCheck);
            Assert.Equal(manifest, options.FixtureManifest);
        }
        finally { File.Delete(manifest); }
    }

    [Fact]
    public void ExhaustiveStepsRequiresPresentationMode() =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--exhaustive-steps", "--media", "fixture.mp4"]));
}
