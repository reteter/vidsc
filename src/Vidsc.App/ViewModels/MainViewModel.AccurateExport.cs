using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Vidsc.Media.Export;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    internal static AccurateCutRequest CreateAccurateRequest(CopySnapshot snapshot)
    {
        // Accurate requests use raw source PTS. Copy discovery would snap to independent access points.
        var origin = RationalTime.FromTicks(snapshot.Origin).AsSeconds();
        long start = ((snapshot.In.AsSeconds() + origin) / snapshot.TimeBase).Ceiling();
        long end = ((snapshot.Out.AsSeconds() + origin) / snapshot.TimeBase).Ceiling();
        return new(new ProgressiveSourceIdentity(snapshot.Identity, snapshot.Generation, snapshot.Stream,
            snapshot.TimeBase, snapshot.Origin), start, end, snapshot.Audio);
    }

    internal bool PreviewSettingsCurrent(CopySnapshot snapshot) => SnapshotCurrent(snapshot)
        && snapshot.Mode == SelectedExportMode && snapshot.Container == SelectedCopyContainer
        && snapshot.Policy == SelectedCopyPolicy && snapshot.Chapters == PreserveCopyChapters
        && snapshot.AllowFullSelectedEncode == AllowFullSelectedEncode
        && EditorUiBoundary(false) is { } start && start.AsSeconds() == snapshot.In.AsSeconds()
        && EditorUiBoundary(true) is { } end && end.AsSeconds() == snapshot.Out.AsSeconds()
        && snapshot.Audio.Select(t => t.StreamIndex).Concat(snapshot.Subtitles).Order()
            .SequenceEqual(CopyTracks.Where(t => t.IsSelected).Select(t => t.StreamIndex).Order());

    private static string DescribeAccuratePlan(CopySnapshot snapshot, QualifiedAccurateCut plan)
    {
        var origin = RationalTime.FromTicks(snapshot.Origin).AsSeconds();
        var requestedStart = snapshot.In.AsSeconds() + origin;
        var requestedEnd = snapshot.Out.AsSeconds() + origin;
        string regions = plan.FullSelectedEncode
            ? $"Entire selected region [{plan.ResolvedStartSeconds}, {plan.ResolvedEndSeconds}) must be encoded; no copied interior. "
                + (snapshot.AllowFullSelectedEncode ? "Explicit selected-region encoding is enabled. " : "Export is blocked until selected-region encoding is explicitly enabled. ")
            : $"Opening encode [{plan.ResolvedStartSeconds}, {plan.CopyStartSeconds}); copied interior [{plan.CopyStartSeconds}, {plan.CopyEndSeconds}); closing encode [{plan.CopyEndSeconds}, {plan.ResolvedEndSeconds}) source s. ";
        return $"Accurate video plan. Requested {requestedStart}–{requestedEnd} source s; frame resolved {plan.ResolvedStartSeconds}–{plan.ResolvedEndSeconds} s; forward deltas {plan.ResolvedStartSeconds - requestedStart}/{plan.ResolvedEndSeconds - requestedEnd} s. "
            + regions + $"Quality: {plan.QualityPolicy}. Source/output mapping: output = source − {plan.Mapping.SourceStartSeconds} + {plan.Mapping.CommonShiftSeconds} s; context epoch {plan.Mapping.SourceWindowEpochSeconds} s. "
            + (plan.IndependentlyQualifiedTerminalEndSeconds is { } terminal ? $"Verified terminal presentation end {terminal} source s. " : "")
            + DescribeAccurateAudio(plan.AudioDetails) + " Chapters/subtitles omitted. Export qualifies again before writing; unsupported cases fail without fallback.";
    }

    private static string DescribeAccurateAudio(IReadOnlyList<AccurateAudioDetails> tracks) => tracks.Count == 0
        ? "No audio selected. "
        : string.Join(" ", tracks.Select(a => $"Audio {a.Timing.StreamIndex}: {a.SampleRate} Hz {a.Layout}; {a.Policy}; copied intersecting packets, not sample-exact. Intended source {a.Timing.IntendedSourceInterval}; retained source {a.Timing.RetainedSourceInterval}; output audible {a.Timing.OutputAudibleInterval}; output retained {a.Timing.OutputRetainedInterval}. Leading/trailing extensions {a.Timing.LeadingExtension}/{a.Timing.TrailingExtension} s; natural-end shortfall {a.Timing.NaturalEndShortfall} s. Required context {a.RequiredContextSamples} samples; declared leading/trailing skip {a.DeclaredLeadingSkipSamples}/{a.DeclaredTrailingSkipSamples}; measured leading discard {a.MeasuredLeadingDiscardSamples}; preroll packets {a.PrerollOrdinals.Count}; {a.ContextPolicy}.")) + " ";

    private async Task<VerifiedCopyExportResult?> ExportAccurateCoreAsync(CopySnapshot snapshot)
    {
        if (snapshot.QualityPolicy != EditorSettings.ReferenceQualityPolicy)
            throw new InvalidOperationException("Unsupported accurate quality policy.");
        if (!AccurateCutVerifier.TryCreate(_toolset, out var verifier, out var reason)) throw new InvalidOperationException(reason);
        CopyOutputPath = null;
        var token = _mediaJobCancellation!.Token;
        CopyExportSummary = "Qualifying accurate regions, reference quality and copied audio…";
        var plan = await Task.Run(() => verifier!.QualifyAsync(snapshot.SourcePath, CreateAccurateRequest(snapshot), token, ObserveMediaProcess), token);
        token.ThrowIfCancellationRequested();
        if (!SnapshotCurrent(snapshot)) throw new OperationCanceledException("Source changed during accurate qualification.");
        CopyExportSummary = DescribeAccuratePlan(snapshot, plan);
        if (plan.FullSelectedEncode && !snapshot.AllowFullSelectedEncode)
            throw new InvalidOperationException(CopyExportSummary);
        var exporter = new VerifiedAccurateExporter(verifier!);
        var result = await Task.Run(() => exporter.ExportAsync(plan, snapshot.Destination,
            Path.Combine(_paths.WorkDirectory, "export-journals"), token, ObserveMediaProcess), token);
        if (!result.ExitConfirmed)
        {
            _mediaJobStuck = true; _retainedMediaJobId = result.JobId; RaiseCommandStates();
        }
        CopyOutputPath = result.Success ? result.Destination : null;
        CopyExportSummary = result.Success
            ? $"Accurate video output validated: sequence, timing, representation and applicable join/interior/boundary quality checks passed. Presentation end {result.PresentationEndSeconds} output s. "
                + DescribeAccuratePlan(snapshot, plan) + $" Measured minimum boundary PSNR {result.MinimumPsnr?.ToString("0.###") ?? "exact/not applicable"}; block SSIM {result.MinimumBlockSsim?.ToString("0.######") ?? "not applicable"}."
            : result.Error ?? "Accurate export failed.";
        return new(result.JobId, result.Success, result.Cancelled, result.ExitConfirmed, result.Destination,
            result.JournalPath, result.Error);
    }
}
