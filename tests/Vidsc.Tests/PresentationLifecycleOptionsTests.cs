using Vidsc.App.Services;
using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationLifecycleOptionsTests
{
    [Fact]
    public void LifecycleRequiresPresentationMode() => Assert.Throws<ArgumentException>(() =>
        CommandLineOptions.Parse(["--presentation-lifecycle", "--media", "a.mp4", "--media", "b.mp4"]));

    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    public void LifecycleRequiresTwoSourcesAndDefaultRemainsSingleSource(bool lifecycle, int sources, bool accepted)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "option-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "manifest.json");
        File.WriteAllText(manifest, "{}");
        try
        {
            var args = new List<string> { "--presentation-check", "--fixture-manifest", manifest,
                "--winapp-path", Environment.ProcessPath!, "--report", Path.Combine(directory, "report.json") };
            if (lifecycle) args.Add("--presentation-lifecycle");
            for (int index = 0; index < sources; index++) args.AddRange(["--media", $"source-{index}.mp4"]);
            if (accepted)
            {
                var options = CommandLineOptions.Parse(args);
                Assert.Equal(lifecycle, options.PresentationLifecycle);
                Assert.False(options.ExhaustiveSteps);
                Assert.Equal(sources, options.MediaPaths.Count);
            }
            else Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
