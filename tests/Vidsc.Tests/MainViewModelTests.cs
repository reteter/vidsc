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
    public void AccurateRequestUsesCommonEpochAndRoundsBothRawBoundariesForward()
    {
        var snapshot = new MainViewModel.CopySnapshot("fixture.mp4", "source-id", 1, 7, 2,
            Rational.Create(1, 24000), TimeSpan.TicksPerSecond * 3,
            new(1001, Rational.Create(1, 48000)), new(3003, Rational.Create(1, 48000)),
            Vidsc.Core.Export.CopyBoundaryPolicy.Expand, Vidsc.Core.Export.CopyContainer.Mp4,
            false, "output.mp4", [], [], Vidsc.Core.Projects.ExportMode.AccurateVideo,
            Vidsc.Core.Projects.EditorSettings.ReferenceQualityPolicy, false);
        var request = MainViewModel.CreateAccurateRequest(snapshot);
        Assert.Equal(72501, request.StartPts);
        Assert.Equal(73502, request.EndPts);
        Assert.Equal(7, request.Source.Generation);
        Assert.Equal(3 * TimeSpan.TicksPerSecond, request.Source.TimelineOriginTicks);
        Assert.Equal(new RationalTime(1001, Rational.Create(1, 48000)), snapshot.In);
    }

    [Fact]
    public async Task EditorPersistsAccurateIntentAndInvalidatesPreviewOnSettingsChanges()
    {
        AppPaths paths = NewPaths(); string source = Path.Combine(paths.WorkDirectory, "fixture.mp4"); File.WriteAllText(source, "fixture");
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithoutCli(paths), paths, Dispatcher.CurrentDispatcher,
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)));
        try
        {
            await vm.OpenAsync(source);
            var snapshot = vm.CaptureCopySnapshot(false);
            Assert.True(vm.PreviewSettingsCurrent(snapshot));
            vm.CopyStartSeconds = "0.123456";
            Assert.False(vm.PreviewSettingsCurrent(snapshot));
            vm.CopyStartSeconds = "0";
            vm.SelectedExportMode = Vidsc.Core.Projects.ExportMode.AccurateVideo;
            vm.AllowFullSelectedEncode = true; vm.PreserveCopyChapters = false;
            Assert.Equal(Vidsc.Core.Projects.ExportMode.StreamCopy, snapshot.Mode);
            Assert.False(vm.PreviewSettingsCurrent(snapshot));
            vm.SaveRecovery(); await vm.RecoverProjectAsync();
            Assert.Equal(Vidsc.Core.Projects.ExportMode.AccurateVideo, vm.SelectedExportMode);
            Assert.True(vm.AllowFullSelectedEncode);
            Assert.False(vm.PreserveCopyChapters);
            Assert.Throws<InvalidOperationException>(() => vm.CaptureCopySnapshot(false));
            Assert.Contains("unavailable", vm.ExportEligibility);
            Assert.Empty(vm.ExportQueue);
            File.Delete(source); await vm.RecoverProjectAsync();
            Assert.Equal(Vidsc.Core.Projects.ExportMode.AccurateVideo, vm.SelectedExportMode);
            Assert.Throws<InvalidOperationException>(vm.QueueCopyExport);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task CompletedPlanIsInvalidatedByTrackAndSegmentSelection()
    {
        AppPaths paths = NewPaths(); string source = Path.Combine(paths.WorkDirectory, "fixture.mp4"); File.WriteAllText(source, "fixture");
        var info = MakeInfo(source);
        var audio = info.PrimaryVideo! with { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 6, SampleRate = 48000, ChannelLayout = "5.1" };
        info = info with { Streams = [.. info.Streams, audio] };
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithoutCli(paths), paths, Dispatcher.CurrentDispatcher,
            (_, _) => Task.FromResult<SourceMediaInfo?>(info));
        try
        {
            await vm.OpenAsync(source);
            vm.AddSegment(); var first = vm.SelectedSegment!;
            vm.CopyStartSeconds = "1"; vm.CopyEndSeconds = "2"; vm.AddSegment();
            // Seed a completed native plan without running native media in this view-model regression.
            var summary = typeof(MainViewModel).GetProperty(nameof(MainViewModel.CopyExportSummary))!;
            summary.SetValue(vm, "Accurate video plan. Audio 1: copied packets.");
            vm.CopyTracks.Single().IsSelected = true;
            Assert.Equal("Settings changed. Qualify the current export plan.", vm.CopyExportSummary);
            summary.SetValue(vm, "Accurate video plan. Resolved second segment.");
            vm.SelectedSegment = first;
            Assert.Equal("Segment changed. Qualify the current export plan.", vm.CopyExportSummary);
            summary.SetValue(vm, "Qualified copy plan with current timing.");
            vm.SelectedSegment = first with { Resolved = new(new(first.RequestedStart, first.RequestedStart), new(first.RequestedEnd, first.RequestedEnd)) };
            Assert.Equal("Qualified copy plan with current timing.", vm.CopyExportSummary);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task EditorRetainsEditsAcrossRecoveryAndRejectsMissingSourceEvenWithOldMetadata()
    {
        AppPaths paths = NewPaths();
        string source = Path.Combine(paths.WorkDirectory, "fixture.mp4"); File.WriteAllText(source, "neutral synthetic source");
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithoutCli(paths), paths, Dispatcher.CurrentDispatcher,
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)));
        try
        {
            await vm.OpenAsync(source);
            vm.SegmentName = "First"; vm.AddSegment();
            vm.CopyStartSeconds = "1"; vm.CopyEndSeconds = "2"; vm.SegmentName = "Second"; vm.AddSegment();
            vm.UndoEditorCommand.Execute(null); Assert.Single(vm.Segments);
            vm.RedoEditorCommand.Execute(null); Assert.Equal(2, vm.Segments.Count);
            vm.CopyStartSeconds = "5"; vm.CopyEndSeconds = "1"; vm.SegmentName = "";
            string project = Path.Combine(paths.WorkDirectory, "test.vidsc.json"); vm.SaveProject(project); vm.SaveRecovery();
            await vm.RecoverProjectAsync(); Assert.Equal(2, vm.Segments.Count);
            Assert.Equal("5", vm.CopyStartSeconds); Assert.Equal("1", vm.CopyEndSeconds); Assert.Equal("", vm.SegmentName);
            File.Delete(source);
            await vm.LoadProjectAsync(project);
            Assert.Equal(2, vm.Segments.Count);
            Assert.Contains("missing or changed", vm.EditorSummary);
            Assert.Throws<InvalidOperationException>(vm.QueueCopyExport);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task BusyPreviewCannotReplaceTheOwnedWorkerTask()
    {
        AppPaths paths = NewPaths();
        var gate = new TaskCompletionSource<Vidsc.Media.Scene.SceneJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths, Dispatcher.CurrentDispatcher,
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)), sceneOverride: (_, _) => gate.Task);
        try
        {
            await vm.OpenAsync(Path.Combine(paths.WorkDirectory, "fixture.mp4"));
            vm.SceneCheckCommand.Execute(null);
            var owned = vm.WaitForMediaJobIdleAsync(); Assert.False(owned.IsCompleted);
            await vm.PreviewCopyAsync(); vm.PreviewCopyCommand.Execute(null);
            Assert.Same(owned, vm.WaitForMediaJobIdleAsync());
        }
        finally
        {
            gate.TrySetResult(new(false, "fixture complete", "fixture", null, null, null, null, null, [], null, null));
            await vm.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConfirmedEditorUsesFreshNativeGetterAndKeepsExactRationalBoundaries()
    {
        AppPaths paths = NewPaths(); string source = Path.Combine(paths.WorkDirectory, "fixture.mp4"); File.WriteAllText(source, "fixture");
        var engine = new FakePlaybackEngine();
        var vm = new MainViewModel(engine, ToolsetWithStubCli(paths), paths, Dispatcher.CurrentDispatcher,
            (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            navigationOverride: (_, _, _, _, _) => Task.FromResult(new Vidsc.Core.Navigation.NavigationMap(Rational.Create(1, 24), 0, [0, 1, 2], [0])));
        try
        {
            await vm.OpenAsync(source); vm.BuildFrameIndexCommand.Execute(null); await vm.WaitForMediaJobIdleAsync();
            var start = new RationalTime(1, Rational.Create(1, 24)); var end = new RationalTime(2, Rational.Create(1, 24));
            engine.ObservedPresentation = new(engine.Positions.Generation, null, start, end, "fresh native fixture", 0, 1, 1);
            Assert.Null(engine.Positions.Snapshot().ObservedPresentation);
            vm.ConfirmBoundary(false); vm.ConfirmBoundary(true); vm.AddSegment();
            Assert.Equal(start.AsSeconds(), vm.Segments[0].RequestedStart.AsSeconds());
            Assert.Equal(end.AsSeconds(), vm.Segments[0].RequestedEnd.AsSeconds());
            engine.ObservedPresentation = null;
            Assert.Throws<InvalidOperationException>(() => vm.ConfirmBoundary(false));
        }
        finally { await vm.ShutdownAsync(); }
    }
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

    [Fact]
    public async Task CancelMediaJobAwaitsTheOwnedScanAndBlocksReplacementUntilItReturns()
    {
        AppPaths paths = NewPaths();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            async (_, _, _, _, ct) => { entered.SetResult(); await release.Task; ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); });
        try
        {
            await vm.OpenAsync(Path.Combine(paths.WorkDirectory, "source.mp4"));
            vm.BuildFrameIndexCommand.Execute(null);
            await entered.Task;
            Task cancel = vm.CancelMediaJobAsync();
            Assert.False(cancel.IsCompleted);
            Assert.True(vm.IsMediaJobActive);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunBenchmarkMediaJobAsync("copy", 1));
            release.SetResult();
            await cancel;
            Assert.False(vm.IsMediaJobActive);
        }
        finally { release.TrySetResult(); await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task UnconfirmedScanExitKeepsTheOwnedSlotBlocked()
    {
        AppPaths paths = NewPaths();
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            (_, _, _, _, _) => Task.FromException<Vidsc.Core.Navigation.NavigationMap>(
                new NavigationProcessStuckException(123, new IOException("termination unconfirmed"))));
        try
        {
            await vm.OpenAsync(Path.Combine(paths.WorkDirectory, "source.mp4"));
            vm.BuildFrameIndexCommand.Execute(null);
            await vm.CancelMediaJobAsync();
            Assert.True(vm.MediaJobStuck);
            Assert.True(vm.IsMediaJobActive);
            Assert.False(vm.ToolCheckCommand.CanExecute(null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunBenchmarkMediaJobAsync("copy", 1));
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ExportCancelsAndDrainsBackgroundInspectionBeforeAnyReplacementWork()
    {
        AppPaths paths = NewPaths();
        string source = Path.Combine(paths.WorkDirectory, "source.mp4");
        File.WriteAllText(source, "synthetic source binding");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        var vm = new MainViewModel(new FakePlaybackEngine(), ToolsetWithStubCli(paths), paths,
            Dispatcher.CurrentDispatcher, (path, _) => Task.FromResult<SourceMediaInfo?>(MakeInfo(path)),
            async (_, _, _, _, ct) =>
            {
                entered.SetResult();
                using var registration = ct.Register(() => cancelled = true);
                await release.Task;
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException();
            });
        try
        {
            await vm.OpenAsync(source);
            vm.CopyDestination = Path.Combine(paths.WorkDirectory, "output.mp4");
            vm.BuildFrameIndexCommand.Execute(null);
            await entered.Task;
            Assert.True(vm.StartCopyExportCommand.CanExecute(null));
            vm.StartCopyExportCommand.Execute(null);
            Task export = vm.WaitForMediaJobIdleAsync();
            Assert.True(cancelled);
            Assert.False(export.IsCompleted);
            Assert.True(vm.StartCopyExportCommand.CanExecute(null)); // More immutable requests may join the queue.
            release.SetResult();
            await export;
            Assert.False(vm.IsMediaJobActive);
            Assert.False(vm.MediaJobStuck);
            Assert.Null(vm.CopyOutputPath);
            Assert.Contains("discovery", vm.CopyExportSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(vm.CopyDestination));
        }
        finally { release.TrySetResult(); await vm.ShutdownAsync(); }
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

    private sealed class FakePlaybackEngine : IPlaybackEngine, IObservedPresentationSource
    {
        public PresentationObservation? ObservedPresentation { get; set; }
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
