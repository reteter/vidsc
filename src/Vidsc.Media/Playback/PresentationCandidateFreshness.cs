using FlyleafLib.MediaFramework.MediaRenderer;
using Vidsc.Core.Playback;
using Vidsc.Core.Navigation;

namespace Vidsc.Media.Playback;

/// <summary>Checks native state at consumption time, after queued notifications may have aged.</summary>
public static class PresentationCandidateFreshness
{
    public static bool IsCurrent(PresentedFrameCandidate candidate, long swapChainEpoch, uint presentId,
        PresentationDiagnostics native, PlaybackPositionSnapshot positions, long presentationEpoch) =>
        PresentationCandidateMapper.IsCurrentOperation(candidate, positions, presentationEpoch)
        && native.CurrentOperation == new PresentationOperation(candidate.Generation.Value,
            candidate.PresentationEpoch, candidate.ResolvedFrom?.Value)
        && native.ActiveSourceGeneration == candidate.Generation.Value
        && native.SwapChainEpoch == swapChainEpoch
        && presentId == native.LastConfirmed;

    public static bool IsSettled(uint presentId, PresentationDiagnostics native, PlaybackPositionSnapshot positions) =>
        positions.PendingRequestId is null && native.PendingCount == 0 && presentId == native.LastSubmitted && presentId == native.LastConfirmed;

    public static PresentationCandidateDiagnostic? Refresh(PresentationCandidateDiagnostic diagnostic,
        PresentationDiagnostics native, PlaybackPositionSnapshot positions, long presentationEpoch,
        PresentationBoundaryMap? map, long? expectedRawPts)
    {
        PresentedFrameCandidate candidate = diagnostic.Candidate;
        if (!IsCurrent(candidate, diagnostic.SwapChainEpoch, diagnostic.PresentId,
            native, positions, presentationEpoch)) return null;

        PresentationObservation? normalized = null;
        if (IsSettled(diagnostic.PresentId, native, positions)
            && (expectedRawPts is null || expectedRawPts == candidate.OriginalPts))
            PresentationCandidateMapper.TryMap(PresentationCandidateMapper.WithBoundaryMap(candidate, map),
                positions, presentationEpoch, out normalized);
        // Always map from raw evidence: settlement can arrive after confirmation, and a removed
        // boundary map must not leave its previous normalized end attached to the candidate.
        return diagnostic with { NormalizedObservation = normalized };
    }
}
