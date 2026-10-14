using Vidsc.Core.Time;

namespace Vidsc.Core.Navigation;

public sealed record ProgressiveSourceIdentity(string SourceId, long Generation, int StreamIndex,
    Rational TimeBase, long TimelineOriginTicks);

public sealed record ProgressiveInspectionRequest(ProgressiveSourceIdentity Source, long StartTicks, long EndTicks);

public sealed record PacketObservation(long? Pts, long? Dts, long? Position, bool IsKey, long? Size = null);
public sealed record FrameObservation(long? Pts, long? Duration, long? PacketPosition);
public sealed record InspectionProbeInterval(long StartTicks, long EndTicks);
public sealed record PacketTraversalFacts(long? FirstPts, long? FirstDts, long? LastPts, long? LastDts, int Count);
public sealed record DecodedFrameCoverage(long FirstPts, long LastPts);
public enum InspectionDependencyState { Unknown }

/// <summary>One actual traversal, in demux/decode order. Extrema never imply continuous coverage.</summary>
public sealed class InspectionTraversal
{
    public ProgressiveInspectionRequest Request { get; }
    public IReadOnlyList<PacketObservation> Packets { get; }
    public IReadOnlyList<FrameObservation> Frames { get; }
    public bool IsComplete { get; }
    public InspectionProbeInterval ProbeInterval { get; }
    public PacketTraversalFacts PacketTraversal { get; }
    /// <summary>Inclusive decoded boundary range from a clean, strictly ordered traversal; never safe-splice proof.</summary>
    public DecodedFrameCoverage? DecodedCoverage { get; }
    public InspectionDependencyState DependencyState => InspectionDependencyState.Unknown;
    public bool HasRequestedDecodedContext { get; }
    public string? MissingRequestedContext { get; }
    public long? PacketLandingPts => Packets.Count == 0 ? null : Packets[0].Pts;
    public long? DecodeLandingPts => Frames.Count == 0 ? null : Frames[0].Pts;
    public InspectionTraversal(ProgressiveInspectionRequest request, IEnumerable<PacketObservation> packets,
        IEnumerable<FrameObservation> frames, bool isComplete, InspectionProbeInterval? probeInterval = null)
    {
        Request = request;
        Packets = Array.AsReadOnly(packets.ToArray());
        Frames = Array.AsReadOnly(frames.ToArray());
        IsComplete = isComplete;
        ProbeInterval = probeInterval ?? new(request.StartTicks, request.EndTicks);
        PacketTraversal = new(Packets.FirstOrDefault()?.Pts, Packets.FirstOrDefault()?.Dts,
            Packets.LastOrDefault()?.Pts, Packets.LastOrDefault()?.Dts, Packets.Count);
        bool ordered = isComplete && Frames.Count >= 2 && Frames.All(f => f.Pts.HasValue);
        for (int i = 1; ordered && i < Frames.Count; i++) ordered = Frames[i].Pts > Frames[i - 1].Pts;
        if (ordered)
        {
            DecodedCoverage = new(Frames[0].Pts!.Value, Frames[^1].Pts!.Value);
            // Compare exact source time to the requested UI time plus its common epoch.
            Rational start = Rational.Create(checked(request.StartTicks + request.Source.TimelineOriginTicks), TimeSpan.TicksPerSecond);
            Rational end = Rational.Create(checked(request.EndTicks + request.Source.TimelineOriginTicks), TimeSpan.TicksPerSecond);
            HasRequestedDecodedContext = Rational.FromInt64(DecodedCoverage.FirstPts) * request.Source.TimeBase <= start &&
                Rational.FromInt64(DecodedCoverage.LastPts) * request.Source.TimeBase >= end;
        }
        MissingRequestedContext = HasRequestedDecodedContext ? null : ordered
            ? "Decoded traversal does not bracket the requested neighborhood; EOF or missing context remains unknown."
            : "A clean traversal with at least two strictly increasing, nonmissing frame PTS is required.";
    }
}

/// <summary>Immutable evidence cache. Completion is traversal completion, never dependency certification.</summary>
public sealed class ProgressiveInspectionSnapshot
{
    public const int CacheVersion = 1;
    public ProgressiveSourceIdentity Source { get; }
    public IReadOnlyList<InspectionTraversal> Traversals { get; }
    public IReadOnlyList<PacketObservation> Packets { get; }
    public IReadOnlyList<FrameObservation> Frames { get; }
    public bool IsComplete => Traversals.Count > 0 && Traversals.All(t => t.IsComplete);
    public bool HasRequestedDecodedContext => Traversals.Count > 0 && Traversals[^1].HasRequestedDecodedContext;
    public bool HasAmbiguousPacketIdentity => Packets.Any(p => p.Position is null);
    public bool HasConflictingPacketIdentity => Packets.Where(p => p.Position.HasValue)
        .GroupBy(p => p.Position).Any(group => group.Distinct().Skip(1).Any());

    public ProgressiveInspectionSnapshot(ProgressiveSourceIdentity source, IEnumerable<InspectionTraversal> traversals)
    {
        Source = source;
        var reads = traversals.ToArray();
        if (reads.Any(t => t.Request.Source != source)) throw new ArgumentException("Source identity mismatch.", nameof(traversals));
        if (reads.Any(t => (long)t.Packets.Count + t.Frames.Count > 200_000))
            throw new InvalidOperationException("One progressive traversal exceeds the cache budget.");
        // Eviction removes evidence, so the discarded region becomes unknown again.
        int first = 0;
        long retained = reads.Sum(t => (long)t.Packets.Count + t.Frames.Count);
        while (reads.Length - first > 64 || retained > 200_000)
        {
            retained -= (long)reads[first].Packets.Count + reads[first].Frames.Count;
            first++;
        }
        reads = reads[first..];
        Traversals = Array.AsReadOnly(reads);
        // Position alone is insufficient when timestamps disagree. Positionless records remain ambiguous.
        var positioned = new HashSet<PacketObservation>();
        Packets = Array.AsReadOnly(reads.SelectMany(t => t.Packets)
            .Where(p => p.Position is null || positioned.Add(p)).ToArray());
        var frames = new HashSet<FrameObservation>();
        Frames = Array.AsReadOnly(reads.SelectMany(t => t.Frames)
            .Where(f => f.PacketPosition is null || frames.Add(f)).ToArray());
    }

    public ProgressiveInspectionSnapshot Merge(ProgressiveInspectionSnapshot other)
    {
        if (Source != other.Source) throw new ArgumentException("Source identity mismatch.", nameof(other));
        return new(Source, Traversals.Concat(other.Traversals));
    }
}
