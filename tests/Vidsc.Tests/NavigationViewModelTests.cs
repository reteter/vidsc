using System.Windows.Threading;

using Vidsc.App.Services;
using Vidsc.App.ViewModels;

using Vidsc.Core.Navigation;
using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;

using Xunit;

namespace Vidsc.Tests;

public sealed class NavigationViewModelTests
{
    private static NavigationMap Map() => new(Rational.Create(1, 1000), 0, [0, 40, 130], [0, 1000, 2000]);

    [Fact]
    public async Task SourceMutationAfterMetadataRejectsInspectionBeforeReadingWithStaleTiming()
    {
        AppPaths paths = NewPaths();
        string source = Path.Combine(paths.WorkDirectory, "source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        int reads = 0;
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            progressiveOverride: (_, request, _) =>
            {
                reads++;
                return Task.FromResult(new ProgressiveInspectionSnapshot(request.Source, []));
            });
        try
        {
            await viewModel.OpenAsync(source);
            File.WriteAllBytes(source, [4, 5, 6]);
            viewModel.InspectNeighborhoodCommand.Execute(null);
            await viewModel.WaitForMediaJobIdleAsync();
            Assert.Equal(0, reads);
            Assert.Null(viewModel.ProgressiveInspection);
            Assert.Contains("Source changed since metadata", viewModel.InspectionSummary);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task SceneReceivesSourceEpochAndSeekCancelsWithoutReleasingSlotEarly()
    {
        AppPaths paths = NewPaths();
        var completion = new TaskCompletionSource<SceneJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SceneJobRequest? request = null;
        CancellationToken token = default;
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path) with
            { ContainerStartTicks = 100_000_000, Streams = [MakeInfo(path).PrimaryVideo! with { Index = 1 }] }),
            sceneOverride: (value, ct) => { request = value; token = ct; return completion.Task; });
        SceneJobResult result = new(true, null, "fixture.mp4", "content", 27, "pyav", null, "pts", [], null, null);
        try
        {
            await viewModel.OpenAsync("fixture.mp4");
            viewModel.SceneCheckCommand.Execute(null);
            Assert.Equal(1, request!.StreamIndex);
            Assert.Equal(10, request.SourceOriginSeconds);
            Assert.Equal(1, request.Generation);
            viewModel.SeekExact(1_000_000);
            Assert.True(token.IsCancellationRequested);
            Assert.True(viewModel.IsMediaJobActive);
            completion.SetResult(result);
            await viewModel.CancelMediaJobAsync();
            Assert.False(viewModel.IsMediaJobActive);
            Assert.Contains("cancelled", viewModel.StatusLine);
        }
        finally { completion.TrySetResult(result); await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task NumericEntryUsesForwardFrameBoundaryAndRejectsUnknownEof()
    {
        AppPaths paths = NewPaths();
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => Task.FromResult(Map()));
        try
        {
            await viewModel.OpenAsync("fixture.mp4");
            viewModel.BuildFrameIndexCommand.Execute(null);
            viewModel.DesiredTimeText = "00:00:00.041";
            viewModel.CommitDesiredExact();
            Assert.Equal(1_300_000, viewModel.DesiredSliderTicks);
            viewModel.DesiredTimeText = "00:00:00.131";
            viewModel.CommitDesiredExact();
            Assert.Contains("EOF", viewModel.LastError);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task SeekingCancelsIndexAndKeepsSlotUntilOwnedWorkConfirmsExit()
    {
        AppPaths paths = NewPaths();
        var completion = new TaskCompletionSource<NavigationMap>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, ct) => { token = ct; return completion.Task; });
        try
        {
            await viewModel.OpenAsync("fixture.mp4");
            viewModel.BuildFrameIndexCommand.Execute(null);
            viewModel.SeekExact(1_000_000);
            Assert.True(token.IsCancellationRequested);
            Assert.True(viewModel.IsMediaJobActive);
            Assert.False(viewModel.InspectNeighborhoodCommand.CanExecute(null));
            completion.SetResult(Map());
            await viewModel.CancelMediaJobAsync();
            Assert.Empty(viewModel.KeyframeTicks);
            Assert.False(viewModel.IsMediaJobActive);
        }
        finally { completion.TrySetResult(Map()); await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task NeighborhoodIsOptInAndPublishesOnlyMatchingSourceEvidence()
    {
        AppPaths paths = NewPaths();
        bool mismatch = false;
        int reads = 0;
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            progressiveOverride: (_, request, _) =>
            {
                reads++;
                var source = mismatch ? request.Source with { Generation = 999 } : request.Source;
                return Task.FromResult(new ProgressiveInspectionSnapshot(source,
                    [new InspectionTraversal(request with { Source = source }, [new(0, -2, 42, true)], [new(0, 1, 42)], true)]));
            });
        try
        {
            await viewModel.OpenAsync("fixture.mp4");
            Assert.Equal(0, reads);
            viewModel.InspectNeighborhoodCommand.Execute(null);
            await viewModel.CancelMediaJobAsync();
            Assert.NotNull(viewModel.ProgressiveInspection);
            Assert.Single(viewModel.ProgressiveInspection.Packets);
            Assert.Single(viewModel.KeyframeTicks);
            Assert.Contains("complete verified frame map", viewModel.InspectionCapabilities);
            var accepted = viewModel.ProgressiveInspection;
            mismatch = true;
            viewModel.InspectNeighborhoodCommand.Execute(null);
            await viewModel.CancelMediaJobAsync();
            Assert.Same(accepted, viewModel.ProgressiveInspection);
            Assert.Contains("different source", viewModel.InspectionSummary);
            await viewModel.OpenAsync("replacement.mp4");
            Assert.Null(viewModel.ProgressiveInspection);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task IndexingOwnsTheSharedJobSlotAndReplacementCancelsAndRejectsStaleMap()
    {
        AppPaths paths = NewPaths();
        var completion = new TaskCompletionSource<NavigationMap>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken indexToken = default;
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, token) => { indexToken = token; return completion.Task; });
        try
        {
            await viewModel.OpenAsync("A.mp4");
            viewModel.BuildFrameIndexCommand.Execute(null);
            Assert.True(viewModel.IsMediaJobActive);
            Assert.False(viewModel.BuildKeyframesCommand.CanExecute(null));
            Assert.False(viewModel.ToolCheckCommand.CanExecute(null));
            viewModel.BuildKeyframesCommand.Execute(null);
            Assert.Equal(1, viewModel.MediaJobRunCount);
            Task replacement = viewModel.OpenAsync("B.mp4");
            Assert.True(indexToken.IsCancellationRequested);
            Assert.False(replacement.IsCompleted);
            completion.SetResult(Map());
            await replacement;
            Assert.Empty(viewModel.KeyframeTicks);
            Assert.Contains("B.mp4", viewModel.SourceSummary);
            Assert.False(viewModel.IsMediaJobActive);
        }
        finally { completion.TrySetResult(Map()); await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task UnconfirmedChildExitKeepsMediaSlotBlockedAfterReplacement()
    {
        AppPaths paths = NewPaths();
        var viewModel = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => throw new NavigationProcessStuckException(12345, new IOException("owned child did not exit")));
        try
        {
            await viewModel.OpenAsync("A.mp4");
            viewModel.BuildFrameIndexCommand.Execute(null);
            Assert.True(viewModel.IsMediaJobActive);
            Assert.Contains("12345", viewModel.NavigationReason);
            Assert.False(viewModel.ToolCheckCommand.CanExecute(null));
            await viewModel.OpenAsync("B.mp4");
            Assert.True(viewModel.IsMediaJobActive);
            Assert.False(viewModel.BuildFrameIndexCommand.CanExecute(null));
            Assert.Contains("12345", viewModel.NavigationReason);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task RepeatedNavigationKeepsOwnedTargetAndReplacementDropsIndex()
    {
        AppPaths paths = NewPaths();
        var engine = new FakePlaybackEngine();
        var viewModel = new MainViewModel(engine, ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => Task.FromResult(Map()));
        try
        {
            await viewModel.OpenAsync("A.mp4");
            viewModel.BuildFrameIndexCommand.Execute(null);
            viewModel.NavigateKeyframe(true);
            engine.Positions.ReportAdvisoryPosition(RationalTime.FromTicks(0), engine.Positions.Generation);
            viewModel.RefreshReadouts();
            viewModel.NavigateKeyframe(true);
            Assert.Equal(TimeSpan.FromSeconds(2).Ticks, viewModel.DesiredSliderTicks);
            viewModel.TimeMode = NavigationTimeMode.FrameIndex;
            viewModel.DesiredTimeText = "2";
            viewModel.CommitDesiredExact();
            Assert.Equal(1_300_000, viewModel.DesiredSliderTicks);
            viewModel.DesiredTimeText = "3";
            viewModel.CommitDesiredExact();
            Assert.NotNull(viewModel.LastError);
            await viewModel.OpenAsync("B.mp4");
            Assert.Empty(viewModel.KeyframeTicks);
            Assert.Contains("unavailable", viewModel.DesiredTimeText);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task PlayingModeAndIndexUpdatesKeepFollowingAdvisoryAndLatestSeekCompletionReleasesTarget()
    {
        AppPaths paths = NewPaths();
        var engine = new FakePlaybackEngine();
        var viewModel = new MainViewModel(engine, ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => Task.FromResult(Map()));
        try
        {
            await viewModel.OpenAsync("A.mp4");
            viewModel.Play();
            await viewModel.WaitForTransportIdleAsync();
            viewModel.TimeMode = NavigationTimeMode.NominalTimecode;
            viewModel.BuildFrameIndexCommand.Execute(null);
            engine.Positions.ReportAdvisoryPosition(RationalTime.FromTicks(500_000), engine.Positions.Generation);
            viewModel.RefreshReadouts();
            Assert.Equal(500_000, viewModel.DesiredSliderTicks);
            viewModel.NavigateKeyframe(true);
            await viewModel.WaitForTransportIdleAsync();
            SeekCommand oldRequest = engine.Positions.Snapshot().LastRequest!;
            viewModel.NavigateKeyframe(true);
            engine.Positions.CompleteSeek(new SeekCompletion(oldRequest.Id, oldRequest.Generation, oldRequest.Requested, true, true));
            engine.Positions.ReportAdvisoryPosition(RationalTime.FromTicks(0), engine.Positions.Generation);
            viewModel.RefreshReadouts();
            Assert.Equal(TimeSpan.FromSeconds(2).Ticks, viewModel.DesiredSliderTicks);
            await viewModel.WaitForTransportIdleAsync();
            SeekCommand request = engine.Positions.Snapshot().LastRequest!;
            engine.Positions.CompleteSeek(new SeekCompletion(request.Id, request.Generation, request.Requested, true, true));
            engine.Positions.ReportAdvisoryPosition(RationalTime.FromTicks(21_000_000), engine.Positions.Generation);
            viewModel.RefreshReadouts();
            Assert.Equal(21_000_000, viewModel.DesiredSliderTicks);
        }
        finally { await viewModel.ShutdownAsync(); }
    }

    [Fact]
    public async Task ShutdownDuringReplacementIndexCleanupNeverOpensReplacement()
    {
        AppPaths paths = NewPaths();
        var engine = new FakePlaybackEngine();
        var completion = new TaskCompletionSource<NavigationMap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(engine, ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => completion.Task);
        await viewModel.OpenAsync("A.mp4");
        viewModel.BuildFrameIndexCommand.Execute(null);
        Task replacement = viewModel.OpenAsync("B.mp4");
        Task shutdown = viewModel.ShutdownAsync();
        completion.SetResult(Map());
        await Task.WhenAll(replacement, shutdown);
        Assert.Equal(new[] { "A.mp4" }, engine.OpenPaths);
        Assert.False(viewModel.IsBusy);
        Assert.True(engine.Disposed);
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
            Positions.RequestSeek(RationalTime.FromTicks(position.Ticks), true);
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

        public void CloseSource()
        {
            Positions.BeginSource();
            Status = PlaybackEngineStatus.Ready;
        }

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
