using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using Vidsc.App.Services;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    public sealed class ExportTrackChoice(int streamIndex, string codec, string label, bool audio) : ObservableObject
    {
        private bool _selected;
        public int StreamIndex { get; } = streamIndex;
        public string Codec { get; } = codec;
        public string Label { get; } = label;
        public bool IsAudio { get; } = audio;
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    }
    private string _copyStart = "0", _copyEnd = "1", _copyDestination = "";
    private CopyBoundaryPolicy _copyPolicy = CopyBoundaryPolicy.Expand;
    private CopyContainer _copyContainer = CopyContainer.Mp4;
    private bool _copyChapters = true;
    private bool _copyExportRequested;
    private readonly Queue<CopyQueueItem> _copyQueue = new();
    public ObservableCollection<CopyQueueItem> ExportQueue { get; } = [];
    internal sealed record CopySnapshot(string SourcePath, string Identity, long OpenToken, long Generation,
        int Stream, Rational TimeBase, long Origin, RationalTime In, RationalTime Out, CopyBoundaryPolicy Policy,
        CopyContainer Container, bool Chapters, string Destination, AudioCopySelection[] Audio, int[] Subtitles,
        ExportMode Mode, string QualityPolicy, bool AllowFullSelectedEncode);
    public sealed class CopyQueueItem : ObservableObject
    {
        private string _status = "Queued";
        internal readonly CopySnapshot Snapshot;
        internal CopyQueueItem(CopySnapshot snapshot) { Snapshot = snapshot; Destination = snapshot.Destination; }
        public string Destination { get; }
        public ExportMode Mode => Snapshot.Mode;
        public string Status { get => _status; internal set => SetProperty(ref _status, value); }
        public string? OutputPath { get; internal set; }
        internal bool IsFinished { get; set; }
    }
    public string CopyStartSeconds { get => _copyStart; set => SetProperty(ref _copyStart, value); }
    public string CopyEndSeconds { get => _copyEnd; set => SetProperty(ref _copyEnd, value); }
    public string CopyDestination { get => _copyDestination; set => SetProperty(ref _copyDestination, value); }
    public CopyBoundaryPolicy SelectedCopyPolicy { get => _copyPolicy; set => SetProperty(ref _copyPolicy, value); }
    public CopyContainer SelectedCopyContainer { get => _copyContainer; set => SetProperty(ref _copyContainer, value); }
    public bool PreserveCopyChapters { get => _copyChapters; set => SetProperty(ref _copyChapters, value); }
    public IReadOnlyList<CopyBoundaryPolicy> CopyPolicies { get; } = Enum.GetValues<CopyBoundaryPolicy>();
    public IReadOnlyList<CopyContainer> CopyContainers { get; } = Enum.GetValues<CopyContainer>();
    public ObservableCollection<ExportTrackChoice> CopyTracks { get; } = [];
    private ExportMode _exportMode;
    private bool _allowFullSelectedEncode;
    public ExportMode SelectedExportMode { get => _exportMode; set { if (SetProperty(ref _exportMode, value)) { CopyExportSummary = "Settings changed. Qualify the current export plan."; RefreshExportEligibility(); } } }
    public bool AllowFullSelectedEncode { get => _allowFullSelectedEncode; set => SetProperty(ref _allowFullSelectedEncode, value); }
    public IReadOnlyList<ExportMode> ExportModes { get; } = Enum.GetValues<ExportMode>();
    public string AccurateQualityPolicy => EditorSettings.ReferenceQualityPolicy;
    public string ExportEligibility => SelectedExportMode == ExportMode.StreamCopy
        ? "Stream copy: selected boundary policy applies; no video is re-encoded."
        : AccurateEligibilityReason() ?? "Accurate video requires native qualification of this exact interval. Reference CRF 18 / slow; no alternative quality profile is qualified. Audio uses intersecting copied packets, never sample-exact. MP4 omits chapters and subtitles.";
    private void RefreshExportEligibility() => OnPropertyChanged(nameof(ExportEligibility));

    private string? AccurateEligibilityReason()
    {
        if (!IsProjectBound()) return "Accurate video unavailable: project source is missing or changed.";
        if (!AccurateCutVerifier.TryCreate(_toolset, out _, out var reason)) return reason;
        if (SelectedCopyContainer != CopyContainer.Mp4) return "Accurate video requires MP4 output.";
        if (!string.Equals(Path.GetExtension(_sourcePath), ".mp4", StringComparison.OrdinalIgnoreCase)) return "Accurate video currently requires an MP4 source.";
        if (PreserveCopyChapters) return "Accurate video omits chapters. Explicitly uncheck Preserve chapters to continue.";
        if (CopyTracks.Any(t => t.IsSelected && !t.IsAudio)) return "Accurate video does not support subtitles. Explicitly deselect them; no conversion is performed.";
        var selected = CopyTracks.Where(t => t.IsSelected && t.IsAudio).ToArray();
        if (selected.Length > 2 || selected.Any(t => t.Codec is not ("aac" or "ac3")
            || _mediaInfo!.Streams.Single(s => s.Index == t.StreamIndex) is not { SampleRate: 48000, Channels: 6 }))
            return "Accurate video supports at most two selected 48-kHz 5.1 AAC/AC-3 tracks. Explicitly deselect unsupported tracks; no conversion is performed.";
        if (_mediaInfo?.PrimaryVideo is not { } video || video.CodecName is not ("h264" or "hevc")
            || video.Width <= 0 || video.Height <= 0 || (long)video.Width * video.Height > 1920 * 1080 || video.RotationDegrees != 0
            || (video.CodecName == "h264" && video.PixelFormat != "yuv420p")
            || (video.CodecName == "hevc" && video.PixelFormat != "yuv420p10le"))
            return "Accurate video requires the qualified AVC 8-bit / HEVC Main10 representation, up to 1920×1080 without rotation. No fallback conversion is performed.";
        return null;
    }
    public RelayCommand StartCopyExportCommand { get; private set; } = null!;
    public RelayCommand OpenCopyOutputCommand { get; private set; } = null!;
    public RelayCommand ReconcileCopyWorkerCommand { get; private set; } = null!;
    public RelayCommand CancelCopyExportCommand { get; private set; } = null!;
    private string _copyExportSummary = "No stream-copy range qualified.";
    private string? _copyOutputPath;
    public string CopyExportSummary { get => _copyExportSummary; private set => SetProperty(ref _copyExportSummary, value); }
    public string? CopyOutputPath { get => _copyOutputPath; private set { if (SetProperty(ref _copyOutputPath, value)) OpenCopyOutputCommand?.RaiseCanExecuteChanged(); } }

    private void InitializeCopyExportCommands()
    {
        StartCopyExportCommand = new RelayCommand(() => EditorAction(QueueCopyExport),
            () => _ffmpeg is not null && _sourcePath is not null && !_isBusy && !_shutdownRequested && !_mediaJobStuck);
        OpenCopyOutputCommand = new RelayCommand(() =>
        {
            try { OpenCopyOutput(); }
            catch (Exception error) { CopyExportSummary = "Opening output failed: " + error.Message; }
        }, () => CopyOutputPath is not null && !_shutdownRequested);
        ReconcileCopyWorkerCommand = new RelayCommand(() =>
        {
            CopyExportSummary = TryReconcileMediaJob() ? "Failed worker cleanup confirmed. You may retry." : "Worker exit/cleanup is still unconfirmed.";
        }, () => _retainedMediaJobId is not null && !_shutdownRequested);
        CancelCopyExportCommand = new RelayCommand(() => { CancelQueuedCopies(); _mediaJobCancellation?.Cancel(); }, () => _mediaJobActive && !_shutdownRequested);
    }

    private void RefreshCopyTracks()
    {
        CopyTracks.Clear();
        if (_mediaInfo is null) return;
        foreach (var stream in _mediaInfo.Streams.Where(s => s.IsAudio || s.CodecType == "subtitle"))
        {
            var choice = new ExportTrackChoice(stream.Index, stream.CodecName,
                $"{stream.CodecType}: {stream.CodecName} {stream.Language ?? "und"} {stream.ChannelLayout}", stream.IsAudio);
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ExportTrackChoice.IsSelected)) return;
                if (IsProjectBound()) _projectTrackSelection = CopyTracks.Where(t => t.IsSelected).Select(t => t.StreamIndex).ToArray();
                ScheduleRecovery();
                RefreshExportEligibility();
                CopyExportSummary = "Settings changed. Qualify the current export plan.";
            };
            CopyTracks.Add(choice);
        }
        RefreshExportEligibility();
        CopyExportSummary = "Source changed. Qualify the current export plan.";
    }

    internal CopySnapshot CaptureCopySnapshot(bool chooseDestination)
    {
        RequireProjectBinding();
        if (SelectedExportMode == ExportMode.AccurateVideo && AccurateEligibilityReason() is { } reason)
            throw new InvalidOperationException(reason);
        if (chooseDestination && string.IsNullOrWhiteSpace(CopyDestination))
        {
            var dialog = new SaveFileDialog { Filter = SelectedCopyContainer == CopyContainer.Mp4 ? "MP4 video|*.mp4" : "Matroska video|*.mkv",
                AddExtension = true, DefaultExt = SelectedCopyContainer == CopyContainer.Mp4 ? ".mp4" : ".mkv", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) throw new OperationCanceledException("Destination selection cancelled.");
            CopyDestination = dialog.FileName;
        }
        var video = _mediaInfo!.PrimaryVideo!;
        var selected = CopyTracks.Where(t => t.IsSelected).ToArray();
        var start = EditorUiBoundary(false) ?? throw new ArgumentException("Invalid In time.");
        var end = EditorUiBoundary(true) ?? throw new ArgumentException("Invalid Out time.");
        if (start >= end) throw new ArgumentException("Out must follow In.");
        return new(_sourcePath!, _metadataIdentity!.IdentityKey, _openToken, _engine.Positions.Generation.Value,
            video.Index, video.TimeBase!.Value, _mediaInfo.ContainerStartTicks ?? 0, start, end, SelectedCopyPolicy,
            SelectedCopyContainer, PreserveCopyChapters, CopyDestination,
            selected.Where(t => t.IsAudio).Select(t => new AudioCopySelection(t.StreamIndex, t.Codec)).ToArray(),
            selected.Where(t => !t.IsAudio).Select(t => t.StreamIndex).ToArray(), SelectedExportMode, AccurateQualityPolicy, AllowFullSelectedEncode);
    }

    public void QueueCopyExport()
    {
        var snapshot = CaptureCopySnapshot(true);
        if (File.Exists(snapshot.Destination)) throw new IOException("Destination exists. Choose a new path; replacement is not enabled.");
        // Retain bounded recent results without letting finished history consume pending capacity.
        while (ExportQueue.Count >= 100 && ExportQueue.FirstOrDefault(j => j.IsFinished) is { } finished)
            ExportQueue.Remove(finished);
        if (ExportQueue.Count >= 100) throw new InvalidOperationException("The queue holds 100 pending exports. Wait for completion or cancel queued work.");
        _recoveryTimer.Stop();
        EditorProjectStore.Save(RecoveryPath, CaptureProject());
        var item = new CopyQueueItem(snapshot); ExportQueue.Add(item); _copyQueue.Enqueue(item);
        if (_copyExportRequested) return;
        var previous = _mediaJobTask; _copyExportRequested = true;
        _mediaJobTask = RunCopyQueueAsync(previous);
    }
    public void CancelQueuedCopies()
    {
        while (_copyQueue.TryDequeue(out var pending)) { pending.Status = "Cancelled before start"; pending.IsFinished = true; }
    }
    private bool SnapshotCurrent(CopySnapshot snapshot) => snapshot.OpenToken == _openToken && snapshot.Identity == _metadataIdentity?.IdentityKey
        && snapshot.Generation == _engine.Positions.Generation.Value && IsProjectBound();

    private async Task RunCopyQueueAsync(Task previous)
    {
        try
        {
            if (!previous.IsCompleted)
            {
                CopyExportSummary = "Cancelling background inspection before export…";
                _mediaJobCancellation?.Cancel();
                await previous;
            }
            if (_mediaJobStuck) { CopyExportSummary = "Previous worker exit is unconfirmed; reconcile it before exporting."; return; }
            while (_copyQueue.TryDequeue(out var item))
            {
                var snapshot = item.Snapshot;
                if (_shutdownRequested || !SnapshotCurrent(snapshot)) { item.Status = "Invalidated by source change"; item.IsFinished = true; continue; }
                item.Status = "Resolving / qualifying / exporting";
                try
                {
                    var result = await ExportSelectedCopyCoreAsync(snapshot);
                    item.OutputPath = result?.Success == true ? result.Destination : null;
                    item.Status = result?.Success == true ? "Completed — validated" : CopyExportSummary;
                }
                finally { item.IsFinished = true; }
                if (_mediaJobStuck) { CancelQueuedCopies(); break; }
            }
        }
        catch (Exception error) { CopyExportSummary = error.Message; }
        finally { CancelQueuedCopies(); _copyExportRequested = false; RaiseCommandStates(); }
    }

    private async Task<VerifiedCopyExportResult?> ExportSelectedCopyCoreAsync(CopySnapshot snapshot)
    {
        if (!SnapshotCurrent(snapshot) || !TryBeginMediaJob()) return null;
        var selectedPolicy = snapshot.Policy;
        var selectedContainer = snapshot.Container;
        var preserveChapters = snapshot.Chapters;
        string destination = snapshot.Destination;
        bool delegated = false;
        try
        {
            if (snapshot.Mode == ExportMode.AccurateVideo)
                return await ExportAccurateCoreAsync(snapshot);
            var video = _mediaInfo?.PrimaryVideo;
            if (video?.TimeBase is not { IsPositive: true } tb || _metadataIdentity is null)
                throw new InvalidOperationException("Reopen source metadata before exporting.");
            if (!CopyRangeDiscovery.TryCreate(_toolset, out var discovery)) throw new InvalidOperationException("Bundled copy discovery is unavailable.");
            var audio = snapshot.Audio;
            var subtitles = snapshot.Subtitles;
            if (subtitles.Any(index => _mediaInfo!.Streams.Single(t => t.Index == index).CodecName != "subrip")) throw new InvalidOperationException("Only native SubRip subtitle copy is qualified.");
            if (selectedContainer == CopyContainer.Mp4 && subtitles.Length > 0)
                throw new InvalidOperationException("Choose Matroska for SubRip/chapters, or explicitly deselect them. No conversion is performed.");
            if (selectedContainer == CopyContainer.Matroska && audio.Any(a => a.CodecName != "eac3"
                && !(video.CodecName is "h264" or "hevc" && a.CodecName is "aac" or "ac3")))
                throw new InvalidOperationException("Matroska audio copy currently qualifies ordinary 48-kHz stereo/5.1 E-AC-3. Deselect other audio tracks or choose a supported container; no conversion is performed.");
            if (selectedContainer != CopyContainer.Matroska && video.CodecName == "av1")
                throw new InvalidOperationException("Choose Matroska output for AV1 stream copy.");
            CopyExportSummary = "Resolving numeric input to observed presentation boundaries…";
            var identity = new ProgressiveSourceIdentity(snapshot.Identity, snapshot.Generation, snapshot.Stream, snapshot.TimeBase, snapshot.Origin);
            var cancellation = _mediaJobCancellation!;
            var found = await Task.Run(() => discovery!.DiscoverAsync(snapshot.SourcePath, identity, snapshot.In,
                snapshot.Out, selectedPolicy, cancellation.Token, ObserveMediaProcess), cancellation.Token);
            if (!SnapshotCurrent(snapshot)) throw new OperationCanceledException("Source changed while discovering export boundaries.");
            delegated = true;
            return await ExportCopyCoreAsync(found.Candidate, found.Requested, selectedPolicy, audio, destination,
                selectedContainer == CopyContainer.Matroska ? new(subtitles, preserveChapters, audio) : null);
        }
        catch (NavigationProcessStuckException error) { RetainMediaJob(error); CopyExportSummary = error.Message; }
        catch (Exception error) { CopyExportSummary = error is OperationCanceledException ? "Export cancelled." : error.Message; }
        finally { if (!delegated) EndMediaJob(); }
        return null;
    }

    public Task PreviewCopyAsync()
    {
        if (!CanRunMediaJob() || _copyExportRequested || _mediaJobStuck)
        {
            CopyExportSummary = "Wait for the current media job before previewing.";
            return Task.CompletedTask;
        }
        return _mediaJobTask = PreviewCopyCoreAsync();
    }

    private async Task PreviewCopyCoreAsync()
    {
        bool owned = false;
        CopySnapshot? previewSnapshot = null;
        try
        {
            var previewSegmentId = SelectedSegment?.Id;
            string previewName = SegmentName;
            var snapshot = CaptureCopySnapshot(false);
            previewSnapshot = snapshot;
            if (!TryBeginMediaJob()) throw new InvalidOperationException("Wait for the current media job before previewing.");
            owned = true;
            if (snapshot.Mode == ExportMode.AccurateVideo)
            {
                if (!AccurateCutVerifier.TryCreate(_toolset, out var accurate, out var reason)) throw new InvalidOperationException(reason);
                CopyExportSummary = "Qualifying accurate video regions, quality and copied audio…";
                var plan = await Task.Run(() => accurate!.QualifyAsync(snapshot.SourcePath, CreateAccurateRequest(snapshot), _mediaJobCancellation!.Token, ObserveMediaProcess));
                if (!SnapshotCurrent(snapshot)) throw new OperationCanceledException("Source changed.");
                if (PreviewSettingsCurrent(snapshot)) CopyExportSummary = DescribeAccuratePlan(snapshot, plan);
                return;
            }
            if (!CopyRangeDiscovery.TryCreate(_toolset, out var discovery) || !FiniteCopyVerifier.TryCreate(_toolset, out var verifier, out _))
                throw new InvalidOperationException("Bundled qualification runtime is unavailable.");
            var identity = new ProgressiveSourceIdentity(snapshot.Identity, snapshot.Generation, snapshot.Stream, snapshot.TimeBase, snapshot.Origin);
            var token = _mediaJobCancellation!.Token;
            CopyExportSummary = "Qualifying copy plan…";
            var found = await Task.Run(() => discovery!.DiscoverAsync(snapshot.SourcePath, identity, snapshot.In, snapshot.Out, snapshot.Policy, token, ObserveMediaProcess), token);
            var video = await Task.Run(() => verifier!.QualifyAsync(snapshot.SourcePath, found.Candidate, Guid.NewGuid(), token, ObserveMediaProcess), token);
            if (!SnapshotCurrent(snapshot)) throw new OperationCanceledException("Source changed.");
            if (!CopyBoundaryPlanner.TryPropose(video.ToObservation().Binding, found.Requested, snapshot.Policy, [video.ToObservation()], out var proposal))
                throw new InvalidOperationException("Verified range does not satisfy this copy policy.");
            if (!PreviewSettingsCurrent(snapshot)) return;
            CopyExportSummary = $"All video copied; no re-encoded portions. Requested {found.Requested.Start.Requested.AsSeconds()}–{found.Requested.End.Requested.AsSeconds()} source s; frame resolved {found.Requested.Start.Resolved.AsSeconds()}–{found.Requested.End.Resolved.AsSeconds()} s; effective copy {proposal!.ResolvedInterval.Start.AsSeconds()}–{proposal.ResolvedInterval.End.AsSeconds()} s; total deltas {proposal.TotalStartDeltaSeconds}/{proposal.TotalEndDeltaSeconds} s. Export requalifies tracks and boundaries.";
            if (SelectedSegment is { } selected && selected.Id == previewSegmentId
                && SegmentName == previewName
                && selected.RequestedStart.AsSeconds() == found.Requested.Start.Requested.AsSeconds()
                && selected.RequestedEnd.AsSeconds() == found.Requested.End.Requested.AsSeconds()
                && EditorUiBoundary(false) is { } currentIn && currentIn.AsSeconds() == snapshot.In.AsSeconds()
                && EditorUiBoundary(true) is { } currentOut && currentOut.AsSeconds() == snapshot.Out.AsSeconds()
                && Segments.IndexOf(selected) is >= 0 and var segmentIndex)
            {
                var resolved = selected with { Resolved = found.Requested };
                Segments[segmentIndex] = resolved; SelectedSegment = resolved; ScheduleRecovery();
            }
        }
        catch (NavigationProcessStuckException error) { RetainMediaJob(error); CopyExportSummary = error.Message; }
        catch (Exception error)
        {
            if (previewSnapshot is null || PreviewSettingsCurrent(previewSnapshot))
                CopyExportSummary = error is OperationCanceledException ? "Plan preview cancelled." : error.Message;
        }
        finally { if (owned) EndMediaJob(); }
    }

    private static RationalTime ParseSeconds(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal seconds)
            || seconds < 0 || seconds * 1_000_000m != decimal.Truncate(seconds * 1_000_000m))
            throw new ArgumentException("Enter nonnegative seconds with at most six decimal places.");
        return new(checked((long)(seconds * 1_000_000m)), Rational.Create(1, 1_000_000));
    }

    /// <summary>Application-owned qualification, planning and export. Callers supply observations, never capabilities.</summary>
    public Task<VerifiedCopyExportResult> ExportCopyAsync(FiniteCopyRequest request,
        ResolvedFrameInterval requested, CopyBoundaryPolicy policy,
        IReadOnlyList<AudioCopySelection> selectedAudio, string destination)
    {
        if (!TryBeginMediaJob()) throw new InvalidOperationException("A source must be open and the media worker must be available.");
        var task = ExportCopyCoreAsync(request, requested, policy, selectedAudio.ToArray(), destination);
        _mediaJobTask = task;
        return task;
    }

    private async Task<VerifiedCopyExportResult> ExportCopyCoreAsync(FiniteCopyRequest request,
        ResolvedFrameInterval requested, CopyBoundaryPolicy policy, IReadOnlyList<AudioCopySelection> selectedAudio,
        string destination, MatroskaCopyOptions? matroska = null)
    {
        var cancellation = _mediaJobCancellation!;
        long generation = _openToken;
        CopyOutputPath = null;
        CopyExportSummary = "Qualifying source packets and fresh decoder context…";
        try
        {
            var sourceVideo = _mediaInfo?.PrimaryVideo;
            if (_metadataIdentity is null || sourceVideo is null || request.Source.SourceId != _metadataIdentity.IdentityKey
                || request.Source.Generation != _engine.Positions.Generation.Value
                || request.Source.StreamIndex != sourceVideo.Index || request.Source.TimeBase != sourceVideo.TimeBase)
                throw new InvalidOperationException("Export request is not bound to the opened source and stream.");
            if (!FiniteCopyVerifier.TryCreate(_toolset, out var finite, out var reason))
                throw new InvalidOperationException(reason);
            PacketAudioCopyVerifier.TryCreate(_toolset, out var audioVerifier, out reason);
            MatroskaCopyVerifier.TryCreate(_toolset, out var matroskaVerifier, out _);
            if (selectedAudio.Count > 0 && !request.Streaming && audioVerifier is null) throw new InvalidOperationException(reason);
            if (request.Streaming)
                matroska ??= new([], false, selectedAudio, CopyContainer.Mp4);
            string source = _sourcePath!;
            var video = await Task.Run(() => finite!.QualifyAsync(source, request, Guid.NewGuid(),
                cancellation.Token, ObserveMediaProcess), cancellation.Token);
            if (generation != _openToken) throw new OperationCanceledException(cancellation.Token);
            if (!CopyBoundaryPlanner.TryPropose(video.ToObservation().Binding, requested, policy,
                [video.ToObservation()], out var proposal))
                throw new InvalidOperationException("The verified finite range does not satisfy the selected boundary policy.");
            CopyExportSummary = $"{policy}: {proposal!.ResolvedInterval.Start.AsSeconds()}–{proposal.ResolvedInterval.End.AsSeconds()} s. "
                + $"Requested boundary deltas: {proposal.TotalStartDeltaSeconds}/{proposal.TotalEndDeltaSeconds} s. Qualifying selected audio…";
            QualifiedAudioCopyRange? audio = selectedAudio.Count == 0 || matroska is not null ? null
                : await Task.Run(() => audioVerifier!.QualifyAsync(source, video, selectedAudio, Guid.NewGuid(),
                    cancellation.Token, ObserveMediaProcess), cancellation.Token);
            var exporter = new VerifiedCopyExporter(finite!, _ffmpeg!, audioVerifier, matroskaVerifier);
            // CopiedTime is dimensioned. Convert through its time base, never treating Value as microseconds.
            var progress = new Progress<CopyExportProgress>(p =>
            {
                if (generation == _openToken && !cancellation.IsCancellationRequested)
                    CopyExportSummary = $"Validating stream-copy output at {p.CopiedTime.AsSeconds()} s…";
            });
            string journals = Path.Combine(_paths.WorkDirectory, "export-journals");
            var result = await Task.Run(() => matroska is not null
                ? exporter.ExportMatroskaVideoAsync(source, video, proposal, matroska, destination, journals, cancellation.Token, ObserveMediaProcess, progress)
                : audio is null
                ? exporter.ExportVideoOnlyAsync(source, video, proposal, destination, journals, cancellation.Token, ObserveMediaProcess, progress)
                : exporter.ExportAudioAsync(source, audio, proposal, destination, journals, cancellation.Token, ObserveMediaProcess, progress));
            if (!result.ExitConfirmed) { _mediaJobStuck = true; _retainedMediaJobId = result.JobId; RaiseCommandStates(); }
            CopyOutputPath = result.Success ? result.Destination : null;
            CopyExportSummary = result.Success
                ? $"Stream-copy output validated by full scan. Presentation end: {result.PresentationEnd?.AsSeconds()} s. {result.AudioTimings?.Count ?? 0} copied audio track(s). "
                    + $"Effective video {proposal!.ResolvedInterval.Start.AsSeconds()}–{proposal.ResolvedInterval.End.AsSeconds()} source s; requested boundary deltas {proposal.TotalStartDeltaSeconds}/{proposal.TotalEndDeltaSeconds} s. "
                    + string.Join(" ", (result.AudioTimings ?? []).Select(t => $"Audio {t.StreamIndex}: packet copy; leading/trailing extensions {t.LeadingExtension}/{t.TrailingExtension} s; not sample-exact."
                        + (t.NaturalEndShortfall.IsPositive ? $" Source audio naturally ends {t.NaturalEndShortfall} s before video; no silence added." : "")))
                    + (result.MatroskaTerminalReconciled ? " MKV terminal end is bound to the video-track duration; raw decoder duration differs by at most one mux tick (up to 1 ms). Exact last-frame hold is player-dependent." : "")
                : result.Error ?? "Stream-copy export failed.";
            return result;
        }
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            CopyExportSummary = exception.Message;
            return new(exception.RetainedJobId ?? Guid.Empty, false, false, false, null, null, exception.Message);
        }
        catch (Exception exception)
        {
            CopyExportSummary = exception is OperationCanceledException ? "Stream-copy export cancelled." : exception.Message;
            return new(Guid.Empty, false, exception is OperationCanceledException, true, null, null, exception.Message);
        }
        finally { EndMediaJob(); }
    }

    public void OpenCopyOutput()
    {
        if (CopyOutputPath is not { } output || !File.Exists(output))
            throw new InvalidOperationException("No validated output is available.");
        Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
    }
}
