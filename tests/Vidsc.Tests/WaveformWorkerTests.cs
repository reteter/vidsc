using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[Collection("Copy job admission")]
public sealed class WaveformWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedAndScriptsRuntimeLayoutsReachOwnedProcessAndReleaseReadGuards(bool scriptsLayout)
    {
        using var fixture = new RuntimeFixture(scriptsLayout, includeNative: true);
        bool started = false, sourceGuarded = false, nativeGuarded = false, driverGuarded = false;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.ReadAsync(_ =>
        {
            started = true;
            sourceGuarded = ReadGuarded(fixture.Source.Path);
            nativeGuarded = ReadGuarded(fixture.NativeLibrary);
            driverGuarded = ReadGuarded(fixture.Driver);
        }));
        Assert.Contains("Waveform worker failed:", error.Message);
        Assert.True(started);
        Assert.True(sourceGuarded); Assert.True(nativeGuarded); Assert.True(driverGuarded);
        Assert.False(ReadGuarded(fixture.Source.Path));
        Assert.False(ReadGuarded(fixture.NativeLibrary));
        Assert.False(ReadGuarded(fixture.Driver));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingOrEmptyPyavDirectoryHasActionableFailureBeforeProcessStart(bool scriptsLayout, bool emptyDirectory)
    {
        using var fixture = new RuntimeFixture(scriptsLayout, includeNative: false);
        if (emptyDirectory) Directory.CreateDirectory(Path.GetDirectoryName(fixture.NativeLibrary)!);
        bool started = false;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.ReadAsync(_ => started = true));
        Assert.Equal("Packaged PyAV libraries missing.", error.Message);
        Assert.False(started);
        Assert.False(ReadGuarded(fixture.Source.Path));
        Assert.False(ReadGuarded(fixture.Driver));
    }

    private static bool ReadGuarded(string path)
    {
        try { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vidsc-waveform-layout-" + Guid.NewGuid().ToString("N"));
        private readonly MediaToolset tools;
        public InspectionSourceIdentity Source { get; }
        public string NativeLibrary { get; }
        public string Driver { get; }

        public RuntimeFixture(bool scriptsLayout, bool includeNative)
        {
            string runtime = Path.Combine(root, "runtime"), scene = Path.Combine(root, "scene");
            string executableDirectory = scriptsLayout ? Path.Combine(runtime, "Scripts") : runtime;
            Directory.CreateDirectory(executableDirectory); Directory.CreateDirectory(scene);
            string python = Path.Combine(executableDirectory, "python.exe");
            // A small native command rejects the worker arguments and exits; no Python/media runtime is used.
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), python);
            Driver = Path.Combine(scene, "vidsc_waveform_job.py"); File.WriteAllText(Driver, "synthetic driver");
            NativeLibrary = Path.Combine(runtime, "Lib", "site-packages", "av.libs", "synthetic.dll");
            if (includeNative)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(NativeLibrary)!);
                File.WriteAllBytes(NativeLibrary, [1, 2, 3]);
            }
            string source = Path.Combine(root, "source.bin"); File.WriteAllBytes(source, [4, 5, 6]);
            Source = InspectionSourceIdentity.Capture(source);
            tools = new(root, "unused", "unused", scene, null, null, python, null, []);
        }

        public Task<WaveformResult> ReadAsync(Action<ToolProcessIdentity> started)
            => WaveformWorker.ReadAsync(tools, Source, 0, 0, 1, 0, 1, 1024, Path.Combine(root, "cache"), default, started);

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private static string Report(long generation = 4, double minimum = -1, long coverage = 960) => JsonSerializer.Serialize(new {
        schemaVersion = 1, success = true, jobId = "job", sourceIdentity = "source", generation, streamIndex = 2,
        sourceOriginSeconds = 10, analyzedStartSeconds = 0, analyzedDurationSeconds = .02,
        requestedBinWidthSamples = 960, binWidthSamples = 960, sampleRate = 48000, channels = 1,
        channelLayout = "mono", sampleOrigin = 0, backend = "pyav", decoderThreads = 2,
        positionOrigin = "native-pts-minus-explicit-origin", conversion = "fltp;same-rate;same-layout;per-channel-min-max",
        build = new { pyav = "19.0.1", driver = "hash" }, gaps = Array.Empty<object>(),
        levels = new[] { new { samplesPerBin = 960, bins = new[] { new { index = 0, firstPts = 480000,
            timeBaseNumerator = 1, timeBaseDenominator = 48000, firstSampleOffset = 0,
            coverageSamples = coverage, peaks = new[] { new[] { minimum, .999969482421875 } } } } } }
    });

    [Fact]
    public void KeepsNativePtsSignedExtremaAndCoverage()
    {
        var result = Parse(Report(coverage: 480));
        Assert.Equal(480000, result.Levels[0].Bins[0].FirstPts);
        Assert.Equal(480, result.Levels[0].Bins[0].CoverageSamples);
        Assert.Equal(-1, result.Levels[0].Bins[0].Peaks[0][0]);
        Assert.Equal("mono", result.ChannelLayout);
    }

    [Fact]
    public void RejectsStaleGenerationMalformedMissingAndExcessCoverage()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Report(generation: 3)));
        Assert.Throws<InvalidDataException>(() => Parse("{}"));
        Assert.Throws<InvalidDataException>(() => Parse("["));
        Assert.Throws<InvalidDataException>(() => Parse(Report(coverage: 961)));
        Assert.Throws<InvalidDataException>(() => Parse(Report(minimum: 2)));
    }

    [Fact]
    public void DecimalStartGridUsesExactValueAndRejectsShiftedBucket()
    {
        var report = JsonNode.Parse(Report())!.AsObject();
        report["analyzedStartSeconds"] = .009; report["sampleOrigin"] = 432;
        report["levels"]![0]!["bins"]![0]!["firstPts"] = 480432;
        Assert.Equal(432, WaveformWorker.Parse(report.ToJsonString(), "cache", "source", "job", 4, 2, 10, .009, .02, 960).SampleOrigin);
        report["levels"]![0]!["bins"]![0]!["index"] = 1;
        Assert.Throws<InvalidDataException>(() => WaveformWorker.Parse(report.ToJsonString(), "cache", "source", "job", 4, 2, 10, .009, .02, 960));
    }

    private static WaveformResult Parse(string json) => WaveformWorker.Parse(json, "cache", "source", "job", 4, 2, 10, 0, .02, 960);
}
