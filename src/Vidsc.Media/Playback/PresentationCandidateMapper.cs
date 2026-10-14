using System.Numerics;

using Vidsc.Core.Playback;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;

namespace Vidsc.Media.Playback;

public enum PresentationStepTargetKind { Unavailable, AdjacentFrame, MapEdge }

/// <summary>
/// Scalar input for the pending renderer integration. This is candidate metadata, not proof that
/// the pinned engine has a trustworthy hook. Capture operation identity before scheduling work.
/// </summary>
public sealed record PresentedFrameCandidate(
    SourceGeneration Generation,
    long PresentationEpoch,
    SeekRequestId? ResolvedFrom,
    int StreamIndex,
    long FrameId,
    long? OriginalPts,
    Rational TimeBase,
    long? SourceEpochTicks,
    long? NextOriginalPts,
    string Evidence);

/// <summary>Validates candidate correlation and normalizes source PTS without floating point.</summary>
public static class PresentationCandidateMapper
{
    public static PresentationStepTargetKind ResolveStepTarget(PresentedFrameCandidate? candidate,
        PlaybackPositionSnapshot current, long currentPresentationEpoch,
        PresentationBoundaryMap? map, bool next, out long uiTicks)
    {
        uiTicks = default;
        if (candidate is null || map is null || current.PendingRequestId is not null
            || !IsCurrentOperation(candidate, current, currentPresentationEpoch)
            || candidate.Generation.Value != map.SourceGeneration || candidate.StreamIndex != map.StreamIndex
            || candidate.TimeBase != map.TimeBase || candidate.SourceEpochTicks != map.TimelineOriginTicks
            || candidate.OriginalPts is not { } pts || !map.Contains(pts))
            return PresentationStepTargetKind.Unavailable;

        if (!map.TryGetAdjacent(candidate.Generation.Value, candidate.StreamIndex, pts, next, out long adjacent))
            return PresentationStepTargetKind.MapEdge;

        uiTicks = map.ToUiTicks(adjacent);
        return PresentationStepTargetKind.AdjacentFrame;
    }

    /// <summary>Completes an unknown end only from a matching complete source-frame index.</summary>
    public static PresentedFrameCandidate WithBoundaryMap(PresentedFrameCandidate candidate,
        PresentationBoundaryMap? map)
    {
        if (candidate.NextOriginalPts is not null || map is null
            || map.TimeBase != candidate.TimeBase || candidate.SourceEpochTicks != map.TimelineOriginTicks
            || candidate.OriginalPts is not { } pts
            || !map.TryResolve(candidate.Generation.Value, candidate.StreamIndex, pts, out long end))
            return candidate;

        return candidate with { NextOriginalPts = end };
    }

    public static bool IsCurrentOperation(PresentedFrameCandidate candidate,
        PlaybackPositionSnapshot current, long currentPresentationEpoch)
        => candidate.Generation.Value > 0 && candidate.Generation == current.Generation
            && candidate.PresentationEpoch == currentPresentationEpoch
            && candidate.ResolvedFrom == current.LastRequest?.Id;

    /// <remarks>
    /// NextOriginalPts must come from the next actual decoded frame in the same stream/decode
    /// epoch or a matching complete source boundary map. Nominal FPS, unvalidated AVFrame duration
    /// and container duration are not substitutes. Unknown final-frame end remains ineligible
    /// until the supplied map carries an explicitly verified terminal-end policy.
    /// This method does not upgrade hook trust or mutate the position tracker.
    /// </remarks>
    public static bool TryMap(
        PresentedFrameCandidate candidate,
        PlaybackPositionSnapshot current,
        long currentPresentationEpoch,
        out PresentationObservation? observation)
    {
        observation = null;
        if (!IsCurrentOperation(candidate, current, currentPresentationEpoch)
            || candidate.StreamIndex < 0 || candidate.FrameId < 0
            || !candidate.TimeBase.IsPositive || string.IsNullOrWhiteSpace(candidate.Evidence)
            || candidate.OriginalPts is not { } start || candidate.NextOriginalPts is not { } end
            || candidate.SourceEpochTicks is not { } sourceEpoch || end <= start)
            return false;

        if (!TryNormalize(start, candidate.TimeBase, sourceEpoch, out RationalTime normalizedStart)
            || !TryNormalize(end, candidate.TimeBase, sourceEpoch, out RationalTime normalizedEnd))
            return false;

        observation = new PresentationObservation(candidate.Generation, candidate.ResolvedFrom,
            normalizedStart, normalizedEnd, candidate.Evidence, candidate.StreamIndex,
            candidate.FrameId, candidate.PresentationEpoch);
        return true;
    }

    private static bool TryNormalize(long pts, Rational timeBase, long sourceEpochTicks, out RationalTime time)
    {
        // Preserve the container epoch exactly even when it is not an integral source-stream unit.
        BigInteger numerator = (BigInteger)pts * timeBase.Numerator * TimeSpan.TicksPerSecond
            - (BigInteger)sourceEpochTicks * timeBase.Denominator;
        BigInteger denominator = (BigInteger)timeBase.Denominator * TimeSpan.TicksPerSecond;
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        numerator /= divisor;
        denominator /= divisor;

        time = default;
        if (numerator < long.MinValue || numerator > long.MaxValue || denominator > long.MaxValue)
            return false;

        time = new RationalTime((long)numerator, Rational.Create(1, (long)denominator));
        return true;
    }
}
