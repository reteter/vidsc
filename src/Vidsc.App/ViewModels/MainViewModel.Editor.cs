using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Microsoft.Win32;
using Vidsc.App.Services;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Playback;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    private ProjectSource? _projectSource;
    private bool _restoringEditor;
    private bool _projectBindingRejected;
    private readonly Stack<EditorSegment[]> _undo = new(), _redo = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private EditorSegment? _selectedSegment;
    private RationalTime? _exactInUi, _exactOutUi;
    private string _segmentName = "Segment 1", _editorSummary = "Open a source to begin editing.";
    private TimelineViewport _viewport = new(0, 1);
    private IReadOnlyList<long> _sceneTicks = [];
    private int[] _projectTrackSelection = [];
    public ObservableCollection<EditorSegment> Segments { get; } = [];
    public EditorSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!SetProperty(ref _selectedSegment, value) || value is null) return;
            bool restoring = _restoringEditor; _restoringEditor = true;
            SegmentName = value.Name;
            _exactInUi = SourceToUi(value.RequestedStart); _exactOutUi = SourceToUi(value.RequestedEnd);
            CopyStartSeconds = DisplaySeconds(_exactInUi.Value); CopyEndSeconds = DisplaySeconds(_exactOutUi.Value);
            _restoringEditor = restoring;
            OnPropertyChanged(nameof(EditorInTicks)); OnPropertyChanged(nameof(EditorOutTicks));
            EditorSummary = DescribeSegment(value);
        }
    }
    public string SegmentName { get => _segmentName; set { if (SetProperty(ref _segmentName, value)) ScheduleRecovery(); } }
    public string EditorSummary { get => _editorSummary; private set => SetProperty(ref _editorSummary, value); }
    public string RecoveryPath => Path.Combine(_paths.WorkDirectory, "editor-recovery.json");
    public string? ProjectPath { get; private set; }
    public TimelineViewport EditorViewport => _viewport;
    public string TimelineScaleText => $"{Math.Max(1, _durationTicks / Math.Max(1, _viewport.Span)):0.##}× · {TimeText.Format((long)_viewport.Span)} visible";
    public double EditorInTicks => EditorUiBoundary(false)?.ToTicks() ?? 0;
    public double EditorOutTicks => EditorUiBoundary(true)?.ToTicks() ?? 0;
    public RelayCommand ConfirmInCommand { get; private set; } = null!;
    public RelayCommand ConfirmOutCommand { get; private set; } = null!;
    public RelayCommand AddSegmentCommand { get; private set; } = null!;
    public RelayCommand ApplySegmentCommand { get; private set; } = null!;
    public RelayCommand RemoveSegmentCommand { get; private set; } = null!;
    public RelayCommand MoveSegmentUpCommand { get; private set; } = null!;
    public RelayCommand MoveSegmentDownCommand { get; private set; } = null!;
    public RelayCommand UndoEditorCommand { get; private set; } = null!;
    public RelayCommand RedoEditorCommand { get; private set; } = null!;
    public RelayCommand SaveProjectCommand { get; private set; } = null!;
    public RelayCommand OpenProjectCommand { get; private set; } = null!;
    public RelayCommand RecoverProjectCommand { get; private set; } = null!;
    public RelayCommand NewProjectCommand { get; private set; } = null!;
    public RelayCommand PreviewCopyCommand { get; private set; } = null!;

    private void InitializeEditor()
    {
        ConfirmInCommand = new(() => EditorAction(() => ConfirmBoundary(false)));
        ConfirmOutCommand = new(() => EditorAction(() => ConfirmBoundary(true)));
        AddSegmentCommand = new(() => EditorAction(AddSegment));
        ApplySegmentCommand = new(() => EditorAction(ApplySegment));
        RemoveSegmentCommand = new(() => EditSegments(() => { if (SelectedSegment is { } s) { Segments.Remove(s); SelectedSegment = Segments.FirstOrDefault(); } }));
        MoveSegmentUpCommand = new(() => MoveSegment(-1)); MoveSegmentDownCommand = new(() => MoveSegment(1));
        UndoEditorCommand = new(() => RestoreHistory(_undo, _redo)); RedoEditorCommand = new(() => RestoreHistory(_redo, _undo));
        SaveProjectCommand = new(() => EditorAction(() =>
        {
            var dialog = new SaveFileDialog { Filter = "vidsc project|*.vidsc.json", DefaultExt = ".vidsc.json", FileName = ProjectPath ?? "project.vidsc.json" };
            if (dialog.ShowDialog() == true) SaveProject(dialog.FileName);
        }));
        OpenProjectCommand = new(() =>
        {
            var dialog = new OpenFileDialog { Filter = "vidsc project|*.vidsc.json|JSON|*.json" };
            if (dialog.ShowDialog() == true) _ = LoadProjectSafelyAsync(dialog.FileName);
        });
        RecoverProjectCommand = new(() => _ = LoadProjectSafelyAsync(RecoveryPath));
        NewProjectCommand = new(() => EditorAction(NewProject));
        PreviewCopyCommand = new(() => _ = PreviewCopyAsync(), () => CanRunMediaJob() && !_copyExportRequested && !_mediaJobStuck);
        LoadThumbnailsCommand = new(() => _mediaJobTask = LoadVisibleThumbnailsAsync(), () => CanRunMediaJob() && !_copyExportRequested && !_mediaJobStuck);
        _recoveryTimer.Tick += (_, _) => { _recoveryTimer.Stop(); SaveRecovery(); };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CopyStartSeconds) or nameof(CopyEndSeconds))
            {
                if (!_restoringEditor) { if (e.PropertyName == nameof(CopyStartSeconds)) _exactInUi = null; else _exactOutUi = null; }
                OnPropertyChanged(nameof(EditorInTicks)); OnPropertyChanged(nameof(EditorOutTicks));
            }
            if (e.PropertyName is nameof(CopyStartSeconds) or nameof(CopyEndSeconds) or nameof(CopyDestination)
                or nameof(SelectedCopyContainer) or nameof(SelectedCopyPolicy) or nameof(PreserveCopyChapters)) ScheduleRecovery();
        };
    }

    private void EditorAction(Action action)
    {
        try { action(); } catch (Exception error) { EditorSummary = error.Message; }
    }
    private RationalTime? EditorUiBoundary(bool isOut)
    {
        try { return isOut ? _exactOutUi ?? ParseSeconds(CopyEndSeconds) : _exactInUi ?? ParseSeconds(CopyStartSeconds); }
        catch (Exception) { return null; }
    }
    private static string DisplaySeconds(RationalTime time) => time.ToSeconds().ToString("0.######", CultureInfo.InvariantCulture);
    private RationalTime UiToSource(RationalTime time)
    {
        var seconds = time.AsSeconds() + RationalTime.FromTicks(_projectSource?.TimelineOriginTicks ?? 0).AsSeconds();
        return new(seconds.Numerator, Rational.Create(1, seconds.Denominator));
    }
    private RationalTime SourceToUi(RationalTime time)
    {
        var seconds = time.AsSeconds() - RationalTime.FromTicks(_projectSource?.TimelineOriginTicks ?? 0).AsSeconds();
        return new(seconds.Numerator, Rational.Create(1, seconds.Denominator));
    }
    private FrameBoundaryResolver? EditorResolver() => _navigationMap?.HasFrameIndex == true && _mediaInfo?.PrimaryVideo is { } video
        ? new(new PresentationBoundaryMap(_engine.Positions.Generation.Value, video.Index, _navigationMap)) : null;

    public void ConfirmBoundary(bool isOut)
    {
        RequireProjectBinding();
        if (!TryGetConfirmedBoundary(isOut, out var time))
            throw new InvalidOperationException("A fresh confirmed presented frame and verified frame map are required. Inspect/build the frame index; seek targets cannot set In/Out.");
        SetEditorBoundary(isOut, SourceToUi(time));
        EditorSummary = isOut ? "Out includes the confirmed frame through its verified presentation end." : "In set to the confirmed presented frame start.";
    }
    private bool TryGetConfirmedBoundary(bool isOut, out RationalTime time)
    {
        time = default;
        if (!IsProjectBound()) return false;
        var resolver = EditorResolver();
        var snapshot = _engine.Positions.Snapshot();
        if (_engine is IObservedPresentationSource observedSource)
        {
            // The native getter rechecks surface/seek freshness; never cache it in the tracker.
            var observed = observedSource.ObservedPresentation;
            snapshot = snapshot with { ObservedPresentation = observed, HookStatus = observed is null ? PresentedFrameHookStatus.Unproven : PresentedFrameHookStatus.Proven,
                ExactBoundariesEligible = observed is not null };
        }
        if (!_transportChain.IsCompleted || Interlocked.Read(ref _navigationDispatchedVersion) != Interlocked.Read(ref _navigationSeekVersion)
            || resolver is null) return false;
        return resolver.TryConfirm(snapshot, _projectSource!.VideoStream, isOut, out time);
    }
    public void SetRequestedBoundary(bool isOut, long uiTicks) => SetEditorBoundary(isOut, RationalTime.FromTicks(Math.Max(0, uiTicks)));
    private void SetEditorBoundary(bool isOut, RationalTime value)
    {
        _restoringEditor = true;
        if (isOut) { _exactOutUi = value; CopyEndSeconds = DisplaySeconds(value); }
        else { _exactInUi = value; CopyStartSeconds = DisplaySeconds(value); }
        _restoringEditor = false;
        OnPropertyChanged(nameof(EditorInTicks)); OnPropertyChanged(nameof(EditorOutTicks)); ScheduleRecovery();
    }

    private EditorSegment DraftSegment(Guid id)
    {
        if (string.IsNullOrWhiteSpace(SegmentName)) throw new InvalidOperationException("Enter a segment name.");
        var start = UiToSource(EditorUiBoundary(false) ?? throw new InvalidOperationException("Invalid In time."));
        var end = UiToSource(EditorUiBoundary(true) ?? throw new InvalidOperationException("Invalid Out time."));
        if (start >= end) throw new InvalidOperationException("Out must follow In.");
        ResolvedFrameInterval? resolved = null;
        if (IsProjectBound()) EditorResolver()?.TryResolveInterval(start, end, out resolved);
        return new(id, SegmentName.Trim(), start, end, resolved);
    }
    public void AddSegment()
    {
        RequireProjectBinding(); var segment = DraftSegment(Guid.NewGuid());
        EditSegments(() => { Segments.Add(segment); SelectedSegment = segment; });
    }
    public void ApplySegment()
    {
        if (SelectedSegment is not { } selected) throw new InvalidOperationException("Select a segment first.");
        var updated = DraftSegment(selected.Id);
        EditSegments(() => { int index = Segments.IndexOf(selected); Segments[index] = updated; SelectedSegment = updated; });
    }
    private void EditSegments(Action action)
    {
        if (_undo.Count >= 50) _undo.Clear();
        _undo.Push(Segments.ToArray()); _redo.Clear(); action(); ScheduleRecovery();
    }
    private void MoveSegment(int delta)
    {
        int index = SelectedSegment is null ? -1 : Segments.IndexOf(SelectedSegment);
        if (index < 0 || index + delta < 0 || index + delta >= Segments.Count) return;
        EditSegments(() => Segments.Move(index, index + delta));
    }
    private void RestoreHistory(Stack<EditorSegment[]> from, Stack<EditorSegment[]> to)
    {
        if (!from.TryPop(out var segments)) return;
        to.Push(Segments.ToArray()); ReplaceSegments(segments); ScheduleRecovery();
    }
    private void ReplaceSegments(IEnumerable<EditorSegment> segments)
    {
        Segments.Clear(); foreach (var segment in segments) Segments.Add(segment); SelectedSegment = Segments.FirstOrDefault();
    }
    private static string DescribeSegment(EditorSegment s) => s.Resolved is { } r
        ? $"Requested {s.RequestedStart.AsSeconds()}–{s.RequestedEnd.AsSeconds()} s; resolved {r.Start.Resolved.AsSeconds()}–{r.End.Resolved.AsSeconds()} s (deltas {r.Start.DeltaSeconds}/{r.End.DeltaSeconds}). Copy plan requires fresh qualification."
        : $"Requested {s.RequestedStart.AsSeconds()}–{s.RequestedEnd.AsSeconds()} source seconds. Resolve/qualify before export.";

    private bool IsProjectBound() => !_projectBindingRejected && _projectSource is { } saved && _metadataIdentity?.IdentityKey == saved.Identity
        && _mediaInfo?.PrimaryVideo?.Index == saved.VideoStream && _sourcePath is not null
        && Path.GetFullPath(_sourcePath).Equals(Path.GetFullPath(saved.Path), StringComparison.OrdinalIgnoreCase);
    private void RequireProjectBinding()
    {
        if (!IsProjectBound()) throw new InvalidOperationException("Project source is missing or changed. Edits are retained; reopen the original source or start a new project explicitly.");
    }
    private void EditorSourceOpened()
    {
        _projectBindingRejected = false;
        _sceneTicks = [];
        if (_projectSource is null && _metadataIdentity is not null && _mediaInfo?.PrimaryVideo is not null) NewProject();
        else
        {
            if (IsProjectBound())
            {
                var selection = _projectTrackSelection;
                foreach (var track in CopyTracks) track.IsSelected = selection.Contains(track.StreamIndex);
            }
            EditorSummary = IsProjectBound() ? "Source rebound. Export boundaries will be qualified again." : "Source differs from the project. Existing edits are retained. Start New project to edit this source.";
        }
    }
    public void NewProject()
    {
        if (_metadataIdentity is null || _mediaInfo?.PrimaryVideo is not { } video) throw new InvalidOperationException("Open source metadata first.");
        SaveRecovery();
        _projectSource = new(_metadataIdentity.Path, _metadataIdentity.IdentityKey, video.Index, _mediaInfo.ContainerStartTicks ?? 0);
        _projectBindingRejected = false;
        _projectTrackSelection = [];
        Segments.Clear(); SelectedSegment = null; _undo.Clear(); _redo.Clear(); ProjectPath = null;
        SegmentName = "Segment 1"; _exactInUi = _exactOutUi = null; CopyStartSeconds = "0"; CopyEndSeconds = "1";
        _viewport = new(0, Math.Max(1, _durationTicks));
        OnPropertyChanged(nameof(EditorViewport)); OnPropertyChanged(nameof(TimelineScaleText));
        EditorSummary = "New project. Set confirmed In/Out or enter numeric times; add a named segment."; ScheduleRecovery();
    }
    public EditorProject CaptureProject() => new(EditorProject.CurrentVersion,
        _projectSource ?? throw new InvalidOperationException("No editor project."), Segments.ToArray(),
        new(SelectedCopyContainer, SelectedCopyPolicy, IsProjectBound() ? CopyTracks.Where(t => t.IsSelected).Select(t => t.StreamIndex).ToArray() : _projectTrackSelection.ToArray(), PreserveCopyChapters, CopyDestination), _viewport)
        { Draft = new(SegmentName, CopyStartSeconds, CopyEndSeconds,
            _exactInUi is { } start ? UiToSource(start) : null,
            _exactOutUi is { } end ? UiToSource(end) : null, SelectedSegment?.Id) };
    public void SaveProject(string path)
    {
        EditorProjectStore.Save(path, CaptureProject()); ProjectPath = Path.GetFullPath(path); EditorSummary = "Project saved. Reopening requires fresh source qualification.";
    }
    public async Task LoadProjectAsync(string path)
    {
        var project = EditorProjectStore.Load(path);
        _recoveryTimer.Stop(); _restoringEditor = true;
        try
        {
            _projectSource = project.Source; ProjectPath = path; _undo.Clear(); _redo.Clear(); ReplaceSegments(project.Segments);
            _projectTrackSelection = project.Settings.SelectedTracks.ToArray();
            SelectedCopyContainer = project.Settings.Container; SelectedCopyPolicy = project.Settings.BoundaryPolicy;
            PreserveCopyChapters = project.Settings.PreserveChapters; CopyDestination = project.Settings.Destination; _viewport = project.Viewport;
            if (project.Draft is { } draft)
            {
                SelectedSegment = Segments.FirstOrDefault(s => s.Id == draft.SelectedSegmentId);
                SegmentName = draft.Name;
                _exactInUi = draft.ExactSourceStart is { } start ? SourceToUi(start) : null;
                _exactOutUi = draft.ExactSourceEnd is { } end ? SourceToUi(end) : null;
                CopyStartSeconds = draft.StartText; CopyEndSeconds = draft.EndText;
            }
            if (!File.Exists(project.Source.Path) || InspectionSourceIdentity.Capture(project.Source.Path).IdentityKey != project.Source.Identity)
            {
                _projectBindingRejected = true;
                _mediaJobCancellation?.Cancel();
                EditorSummary = "Saved source is missing or changed. Edits retained; exports disabled until the original binding is restored.";
                return;
            }
            await OpenAsync(project.Source.Path);
            _viewport = project.Viewport;
            foreach (var track in CopyTracks) track.IsSelected = project.Settings.SelectedTracks.Contains(track.StreamIndex);
            RequireProjectBinding(); EditorSummary = "Project reopened. Saved boundaries are edit history; export will resolve and qualify them again.";
        }
        finally { _restoringEditor = false; OnPropertyChanged(nameof(EditorViewport)); OnPropertyChanged(nameof(TimelineScaleText)); OnPropertyChanged(nameof(EditorInTicks)); OnPropertyChanged(nameof(EditorOutTicks)); }
    }
    private async Task LoadProjectSafelyAsync(string path)
    {
        try { await LoadProjectAsync(path); } catch (Exception error) { EditorSummary = "Project open failed: " + error.Message; }
    }
    public Task RecoverProjectAsync() => LoadProjectAsync(RecoveryPath);
    public void SetEditorViewport(double start, double span)
    {
        if (_viewport.Start == Math.Max(0, start) && _viewport.Span == Math.Max(1, span)) return;
        ++_thumbnailVersion;
        _indexCancellation?.Cancel();
        _viewport = new(Math.Max(0, start), Math.Max(1, span)); ScheduleRecovery();
        OnPropertyChanged(nameof(TimelineScaleText));
    }
    private void ScheduleRecovery()
    {
        if (_restoringEditor || _shutdownRequested || _projectSource is null) return;
        _recoveryTimer.Stop(); _recoveryTimer.Start();
    }
    public void SaveRecovery()
    {
        if (_projectSource is null) return;
        try { EditorProjectStore.Save(RecoveryPath, CaptureProject()); }
        catch (Exception error) { EditorSummary = "Recovery save failed: " + error.Message; }
    }
    public void JumpTime(int seconds) => SeekExact(Math.Clamp((long)DesiredSliderTicks + seconds * TimeSpan.TicksPerSecond, 0, Math.Max(0, _durationTicks)));
    public void NavigateScene(bool next)
    {
        long current = (long)DesiredSliderTicks;
        var targets = next ? _sceneTicks.Where(t => t > current) : _sceneTicks.Reverse().Where(t => t < current);
        if (targets.FirstOrDefault(-1) is >= 0 and var target) SeekExact(target);
        else StatusLine = "No analyzed scene boundary in that direction. Run opt-in scene analysis first.";
    }
}
