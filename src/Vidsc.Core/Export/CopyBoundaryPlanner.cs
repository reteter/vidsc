using Vidsc.Core.Navigation;
using Vidsc.Core.Time;

namespace Vidsc.Core.Export;

public enum CopyBoundaryPolicy { Expand, Contract, Nearest }

public sealed record CopyPlanningSource(ProgressiveSourceIdentity Source, string StreamConfigurationId);

/// <summary>
/// Caller-supplied whole-range observation for draft selection. This transparent data is not
/// dependency certification and cannot authorize export or output promotion.
/// Packet ordinals refer to the immutable manifest identified by PacketManifestId.
/// </summary>
public sealed record CopyRangeObservation(string ObservationId, CopyPlanningSource Binding,
    MediaInterval Interval, string PacketManifestId, long FirstPacketOrdinal, long LastPacketOrdinal);

/// <summary>A timing proposal only; the media verifier must independently establish eligibility.</summary>
public sealed record CopyBoundaryProposal(ResolvedFrameInterval Requested, CopyRangeObservation Selected)
{
    public MediaInterval ResolvedInterval => Selected.Interval;
    public Rational CopySnapStartDeltaSeconds => ResolvedInterval.Start.AsSeconds() - Requested.Interval.Start.AsSeconds();
    public Rational CopySnapEndDeltaSeconds => ResolvedInterval.End.AsSeconds() - Requested.Interval.End.AsSeconds();
    public Rational TotalStartDeltaSeconds => ResolvedInterval.Start.AsSeconds() - Requested.Start.Requested.AsSeconds();
    public Rational TotalEndDeltaSeconds => ResolvedInterval.End.AsSeconds() - Requested.End.Requested.AsSeconds();
    public RationalTime OutputEpoch => ResolvedInterval.Start;
}

/// <summary>Selects an indivisible observed range without deriving boundaries from key flags.</summary>
public static class CopyBoundaryPlanner
{
    /// <exception cref="OverflowException">Exact times or ranking distances exceed the long-based rational range.</exception>
    public static bool TryPropose(CopyPlanningSource source, ResolvedFrameInterval requested,
        CopyBoundaryPolicy policy, IEnumerable<CopyRangeObservation> observations,
        out CopyBoundaryProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(observations);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        proposal = null;
        if (!source.Source.TimeBase.IsPositive || source.Source.StreamIndex < 0
            || string.IsNullOrWhiteSpace(source.Source.SourceId)
            || string.IsNullOrWhiteSpace(source.StreamConfigurationId)
            || !requested.Start.Requested.TimeBase.IsPositive || !requested.End.Requested.TimeBase.IsPositive
            || !requested.Start.Resolved.TimeBase.IsPositive || !requested.End.Resolved.TimeBase.IsPositive
            || requested.Start.Requested >= requested.End.Requested
            || requested.Start.Resolved >= requested.End.Resolved) return false;

        MediaInterval target = requested.Interval;
        CopyRangeObservation? best = null;
        Rational bestDistance = default;
        foreach (var candidate in observations)
        {
            // A stale observation cannot poison other current observations or be silently rebound.
            if (candidate.Binding != source || candidate.Interval.IsEmpty
                || candidate.Interval.Start.TimeBase != source.Source.TimeBase
                || candidate.Interval.End.TimeBase != source.Source.TimeBase
                || string.IsNullOrWhiteSpace(candidate.ObservationId)
                || string.IsNullOrWhiteSpace(candidate.PacketManifestId)
                || candidate.FirstPacketOrdinal < 0 || candidate.LastPacketOrdinal < candidate.FirstPacketOrdinal)
                continue;
            bool expands = Contains(candidate.Interval, target);
            bool contracts = Contains(target, candidate.Interval);
            if (policy == CopyBoundaryPolicy.Expand && !expands
                || policy == CopyBoundaryPolicy.Contract && !contracts) continue;

            Rational distance = (candidate.Interval.Start.AsSeconds() - target.Start.AsSeconds()).Abs()
                + (candidate.Interval.End.AsSeconds() - target.End.AsSeconds()).Abs();
            if (best is null || distance < bestDistance
                || distance == bestDistance && CompareTie(candidate, best, target) < 0)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        if (best is null) return false;
        proposal = new(requested, best);
        return true;
    }

    private static bool Contains(MediaInterval outer, MediaInterval inner)
        => outer.Start <= inner.Start && outer.End >= inner.End;

    private static int CompareTie(CopyRangeObservation left, CopyRangeObservation right, MediaInterval target)
    {
        int result = Contains(right.Interval, target).CompareTo(Contains(left.Interval, target));
        if (result != 0) return result;
        result = left.Interval.Start.CompareTo(right.Interval.Start);
        if (result != 0) return result;
        result = right.Interval.End.CompareTo(left.Interval.End);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.ObservationId, right.ObservationId);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.PacketManifestId, right.PacketManifestId);
        if (result != 0) return result;
        result = left.FirstPacketOrdinal.CompareTo(right.FirstPacketOrdinal);
        return result != 0 ? result : left.LastPacketOrdinal.CompareTo(right.LastPacketOrdinal);
    }
}
