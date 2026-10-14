using System.Windows.Threading;

using Vidsc.App.Services;
using Vidsc.App.ViewModels;

using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;
using Vidsc.Media.Tooling;

using Xunit;

namespace Vidsc.Tests;

/// <summary>
/// View-model regressions for source-switch invalidation, the single owned media job and shutdown.
/// </summary>
public sealed class MainViewModelTests
{
    [Fact]
    public async Task SourceReplacementDropsQueuedTransportFromThePreviousSource()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var engine = new FakePlaybackEngine
        {
            StepAction = () => { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); },
        };
        AppPaths paths = NewPaths();
        var viewModel = new MainViewModel(engine, ToolsetWithoutCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)));
        try
        {
            await viewModel.OpenAsync(Path.Combine(paths.WorkDirectory, "A.mp4"));
            viewModel.StepForward();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.SeekExact(10_000);
            await viewModel.OpenAsync(Path.Combine(paths.WorkDirectory, "B.mp4"));
            release.Set();
            await viewModel.WaitForTransportIdleAsync();
            Assert.Equal(0, engine.ExactSeekCount);
        }
        finally
        {
            release.Set();
            await viewModel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SupersededProbeCannotOverwriteTheNewSourceState()
    {
        var engine = new FakePlaybackEngine();
        AppPaths paths = NewPaths();
        MediaToolset toolset = ToolsetWithoutCli(paths);

        var gateForA = new TaskCompletionSource<SourceMediaInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, CancellationToken, Task<SourceMediaInfo?>> probe = (path, _) =>
            Path.GetFileName(path).StartsWith("A", StringComparison.OrdinalIgnoreCase)
                ? gateForA.Task
                : Task.FromResult<SourceMediaInfo?>(MakeInfo(path));

        var viewModel = new MainViewModel(engine, toolset, paths, Dispatcher.CurrentDispatcher, probe);

        try
        {
            string pathA = Path.Combine(paths.WorkDirectory, "A.mp4");
            string pathB = Path.Combine(paths.WorkDirectory, "B.mp4");

            Task openA = viewModel.OpenAsync(pathA);
            Task openB = viewModel.OpenAsync(pathB);

            await openB;

            // A's probe resolves last, but A was superseded by B and must not publish its state.
            gateForA.SetResult(MakeInfo(pathA));
            await openA;

            Assert.Contains("B.mp4", viewModel.SourceSummary);
            Assert.DoesNotContain("A.mp4", viewModel.SourceSummary);
            Assert.Equal(new[] { pathA, pathB }, engine.OpenPaths);
            Assert.Null(viewModel.LastError);
        }
        finally
        {
            await viewModel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RepeatedToolChecksDoNotStartParallelJobs()
    {
        var engine = new FakePlaybackEngine();
        AppPaths paths = NewPaths();
        MediaToolset toolset = ToolsetWithStubCli(paths);

        Func<string, CancellationToken, Task<SourceMediaInfo?>> probe =
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path));

        var viewModel = new MainViewModel(engine, toolset, paths, Dispatcher.CurrentDispatcher, probe);

        try
        {
            // The source must exist so the owned check actually hashes it (a real async gap); the
            // probe override supplies the metadata used by the view model.
            string sourcePath = Path.Combine(paths.WorkDirectory, "source.mp4");
            File.WriteAllBytes(sourcePath, new byte[4 * 1024 * 1024]);

            await viewModel.OpenAsync(sourcePath);

            Assert.True(viewModel.ToolCheckCommand.CanExecute(null));

            viewModel.ToolCheckCommand.Execute(null);

            // The job owns the slot synchronously, before its first real await.
            Assert.True(viewModel.IsMediaJobActive);
            Assert.Equal(1, viewModel.MediaJobRunCount);
            Assert.False(viewModel.ToolCheckCommand.CanExecute(null));

            // A repeated click must not start a second job.
            viewModel.ToolCheckCommand.Execute(null);
            Assert.Equal(1, viewModel.MediaJobRunCount);

            await WaitUntilAsync(() => !viewModel.IsMediaJobActive, TimeSpan.FromSeconds(60));

            Assert.False(viewModel.IsMediaJobActive);
            Assert.Equal(1, viewModel.MediaJobRunCount);
            Assert.True(viewModel.ToolCheckCommand.CanExecute(null));
        }
        finally
        {
            await viewModel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ShutdownDisablesCommandsAndDisposesTheEngineOnce()
    {
        var engine = new FakePlaybackEngine();
        AppPaths paths = NewPaths();

        Func<string, CancellationToken, Task<SourceMediaInfo?>> probe =
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path));

        var viewModel = new MainViewModel(engine, ToolsetWithoutCli(paths), paths, Dispatcher.CurrentDispatcher, probe);

        await viewModel.OpenAsync(Path.Combine(paths.WorkDirectory, "source.mp4"));

        await viewModel.ShutdownAsync();
        await viewModel.ShutdownAsync();

        Assert.False(viewModel.OpenCommand.CanExecute(null));
        Assert.False(viewModel.ToolCheckCommand.CanExecute(null));
        Assert.True(engine.Disposed);
        Assert.Equal(1, engine.DisposeCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.True(condition(), "The condition was not met within " + timeout);
    }

    private static AppPaths NewPaths()
    {
        string work = Path.Combine(Path.GetTempPath(), "vidsc-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        return new AppPaths(AppContext.BaseDirectory, work, Path.Combine(work, "logs"), Path.Combine(work, "cache"));
    }

    private static MediaToolset ToolsetWithoutCli(AppPaths paths)
        => new(
            paths.ApplicationDirectory,
            Path.Combine(paths.WorkDirectory, "native", "flyleaf"),
            Path.Combine(paths.WorkDirectory, "tools", "ffmpeg"),
            Path.Combine(paths.WorkDirectory, "scene"),
            null,
            null,
            null,
            null,
            Array.Empty<string>());

    private static MediaToolset ToolsetWithStubCli(AppPaths paths)
    {
        string ffmpegDir = Path.Combine(paths.WorkDirectory, "tools", "ffmpeg");
        Directory.CreateDirectory(ffmpegDir);

        string ffmpeg = Path.Combine(ffmpegDir, "ffmpeg.exe");
        string ffprobe = Path.Combine(ffmpegDir, "ffprobe.exe");
        File.WriteAllText(ffmpeg, "stub");
        File.WriteAllText(ffprobe, "stub");

        Directory.CreateDirectory(paths.CacheDirectory);

        return new MediaToolset(
            paths.ApplicationDirectory,
            Path.Combine(paths.WorkDirectory, "native", "flyleaf"),
            ffmpegDir,
            Path.Combine(paths.WorkDirectory, "scene"),
            ffmpeg,
            ffprobe,
            null,
            null,
            Array.Empty<string>());
    }

    private static SourceMediaInfo MakeInfo(string path)
        => new(
            path,
            1024,
            TimeSpan.FromSeconds(3).Ticks,
            new[]
            {
                new MediaStreamInfo(
                    Index: 0,
                    CodecType: "video",
                    CodecName: "h264",
                    Profile: "High",
                    Width: 320,
                    Height: 180,
                    PixelFormat: "yuv420p",
                    RotationDegrees: 0,
                    TimeBase: Rational.Create(1, 24),
                    AverageFrameRate: Rational.Create(24, 1),
                    RealFrameRate: Rational.Create(24, 1),
                    DurationTicks: TimeSpan.FromSeconds(3).Ticks,
                    Channels: null,
                    SampleRate: null,
                    ChannelLayout: null,
                    Language: null),
            });

    private sealed class FakePlaybackEngine : IPlaybackEngine
    {
        public PlaybackPositionTracker Positions { get; } = new();

        public PlaybackEngineStatus Status { get; private set; } = PlaybackEngineStatus.Ready;

        public int CoalescedSeekCount => 0;

        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public List<string> OpenPaths { get; } = new();
        public Action? StepAction { get; init; }
        public int ExactSeekCount { get; private set; }

        public int Volume { get; private set; } = 80;

        public bool IsMuted => false;

        public event EventHandler? StateChanged;

        public Task<OpenSourceResult> OpenAsync(string path, CancellationToken cancellationToken)
        {
            OpenPaths.Add(path);
            Positions.BeginSource();
            Status = PlaybackEngineStatus.Paused;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new OpenSourceResult(true, null, TimeSpan.FromSeconds(3).Ticks));
        }

        public void Play() => Status = PlaybackEngineStatus.Playing;

        public void Pause() => Status = PlaybackEngineStatus.Paused;

        public void TogglePlayPause()
            => Status = Status == PlaybackEngineStatus.Playing ? PlaybackEngineStatus.Paused : PlaybackEngineStatus.Playing;

        public void SeekApproximate(TimeSpan position)
        {
        }

        public void SeekExact(TimeSpan position)
        {
            ExactSeekCount++;
        }

        public void StepForward()
        {
            StepAction?.Invoke();
        }

        public void StepBackward()
        {
        }

        public void SetVolume(int volume) => Volume = volume;

        public void SetMuted(bool muted)
        {
        }

        public void SetRotationDegrees(int degrees)
        {
        }

        public void Stop() => Status = PlaybackEngineStatus.Ready;

        public PlaybackDiagnostics GetDiagnostics() => new(
            Status: Status,
            EngineStarted: true,
            FfmpegFolder: null,
            FfmpegVersion: null,
            ConfiguredAdapter: null,
            AvailableAdapters: Array.Empty<string>(),
            VideoProcessorName: null,
            VideoAccelerated: false,
            DecoderCodecName: "h264",
            DecoderIsHardware: false,
            RotationDegrees: 0,
            SourceFpsNominal: 24,
            CurrentFps: 0,
            FramesDisplayed: 0,
            FramesDropped: 0,
            DurationTicks: TimeSpan.FromSeconds(3).Ticks,
            AdvisoryCurTimeTicks: 0,
            AudioOpened: false,
            AudioChannelsOut: 0,
            AudioSampleRate: 0,
            Volume: Volume,
            CoalescedSeekCount: 0,
            HookStatus: PresentedFrameHookStatus.AdvisoryOnly,
            HookEvidence: "test",
            ExactBoundariesEligible: false,
            LastError: null);

        public void Dispose() => DisposeCount++;
    }
}
