using Vidsc.Media.Tooling;

using Xunit;

namespace Vidsc.Tests;

public sealed class FfmpegCliArgumentTests
{
    private static FfmpegCli CreateCli(string root)
        => new(Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "ffprobe.exe"));

    [Fact]
    public void BuildTinyCopyExportArguments_UsesNoClobberAndNeverOverwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-cli-args-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<string> arguments = CreateCli(root)
            .BuildTinyCopyExportArguments(Path.Combine(root, "in.mp4"), Path.Combine(root, "out.mp4"), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.Contains("-n", arguments);
        Assert.DoesNotContain("-y", arguments);
        // The CLI must copy, never re-encode.
        Assert.Contains("copy", arguments);
    }

    [Fact]
    public void BuildTinyCopyExportArguments_RejectsASourceDestinationAlias()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-cli-args-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "same.mp4");

        Assert.Throws<ArgumentException>(() =>
            CreateCli(root).BuildTinyCopyExportArguments(path, path, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void PathsResolveToSameFile_IsCanonicalAndCaseInsensitive()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vidsc", "Alias");
        Assert.True(FfmpegCli.PathsResolveToSameFile(
            Path.Combine(root, "clip.mp4"),
            Path.Combine(Path.GetTempPath(), "vidsc", "alias", "clip.mp4")));
        Assert.False(FfmpegCli.PathsResolveToSameFile(
            Path.Combine(root, "clip.mp4"),
            Path.Combine(root, "other.mp4")));
        Assert.False(FfmpegCli.PathsResolveToSameFile("", Path.Combine(root, "clip.mp4")));
    }
}

public sealed class PackagedCliCheckTests
{
    private static FfmpegCli MissingCli(string root)
        => new(Path.Combine(root, "missing-ffmpeg.exe"), Path.Combine(root, "missing-ffprobe.exe"));

    [Fact]
    public async Task PreviousCheckCopyOpenedAsSource_IsNeverDeletedOrOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-cli-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // The exact file the old implementation created and deleted: a previous check-copy that
            // the user could legitimately have opened as the current source.
            string previousCopy = Path.Combine(root, "toolcheck-copy.mp4");
            byte[] payload = { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(previousCopy, payload);

            var checker = new PackagedCliCheck(MissingCli(root), root);
            CliCopyExportJob job = checker.CreateCopyExportJob(previousCopy, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            Assert.NotEqual(previousCopy, job.Destination);
            Assert.StartsWith(Path.Combine(root, "cli-check"), job.Destination, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(job.Destination));

            CliCopyExportResult run = await checker.RunCopyExportAsync(job, CancellationToken.None);

            Assert.False(run.Succeeded);
            Assert.True(run.SourceUnchanged);
            Assert.False(run.DestinationCreated);

            // The previous copy (now the source) is byte-identical; nothing was deleted.
            Assert.True(File.Exists(previousCopy));
            Assert.Equal(payload, File.ReadAllBytes(previousCopy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreexistingDestination_IsPreservedBecauseTheCliRunsWithNoClobber()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-cli-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string source = Path.Combine(root, "source.mp4");
            File.WriteAllBytes(source, new byte[] { 7, 7, 7 });

            string destination = Path.Combine(root, "preexisting-copy.mp4");
            byte[] existing = { 42, 42 };
            File.WriteAllBytes(destination, existing);

            var cli = MissingCli(root);
            IReadOnlyList<string> arguments = cli.BuildTinyCopyExportArguments(source, destination, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.Contains("-n", arguments);

            // Attempting the run (the executable is absent) must not touch the existing destination.
            ToolRunResult run = await cli.RunAsync(arguments, CancellationToken.None);
            Assert.False(run.Started);

            Assert.True(File.Exists(destination));
            Assert.Equal(existing, File.ReadAllBytes(destination));
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
