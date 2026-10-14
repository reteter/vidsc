namespace Vidsc.Core.Navigation;

/// <summary>
/// Independently established source-frame expectations, not probe/container metadata.
/// The caller owns the reference's provenance (for example a synthetic fixture specification).
/// Merely copying a decoded scan or labeling metadata does not establish independence.
/// </summary>
public sealed class IndependentTerminalReference
{
    public IndependentTerminalReference(ProgressiveSourceIdentity source, string referenceId,
        string provenance, IEnumerable<long> framePts, long endPts)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ReferenceId = referenceId;
        Provenance = provenance;
        FramePts = Array.AsReadOnly(framePts.ToArray());
        EndPts = endPts;
    }

    public ProgressiveSourceIdentity Source { get; }
    public string ReferenceId { get; }
    public string Provenance { get; }
    public IReadOnlyList<long> FramePts { get; }
    public long EndPts { get; }
}

/// <summary>A source-bound terminal end qualified against an independent complete reference.</summary>
public sealed class TerminalEndQualification
{
    private readonly IndependentTerminalReference _reference;

    private TerminalEndQualification(IndependentTerminalReference reference) => _reference = reference;

    public ProgressiveSourceIdentity Source => _reference.Source;
    public long EndPts => _reference.EndPts;
    public string ReferenceId => _reference.ReferenceId;
    public string Provenance => _reference.Provenance;

    public static bool TryQualify(ProgressiveSourceIdentity source, NavigationMap navigation,
        IndependentTerminalReference? reference, out TerminalEndQualification? qualification,
        out string? unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(navigation);
        qualification = null;
        unavailableReason = Validate(source, navigation, reference);
        if (unavailableReason is not null) return false;
        qualification = new(reference!);
        return true;
    }

    /// <summary>Rechecks identity and complete evidence before a consumer accepts this end.</summary>
    public bool Matches(ProgressiveSourceIdentity source, NavigationMap navigation)
        => Validate(source, navigation, _reference) is null;

    private static string? Validate(ProgressiveSourceIdentity source, NavigationMap navigation,
        IndependentTerminalReference? reference)
    {
        if (reference is null) return "No independent terminal reference is available.";
        if (string.IsNullOrWhiteSpace(reference.ReferenceId) || string.IsNullOrWhiteSpace(reference.Provenance))
            return "The independent reference must identify its origin and provenance.";
        if (string.IsNullOrWhiteSpace(source.SourceId) || source.StreamIndex < 0 || !source.TimeBase.IsPositive
            || source != reference.Source || source.TimeBase != navigation.TimeBase
            || source.TimelineOriginTicks != navigation.TimelineOriginTicks)
            return "Terminal evidence does not match the opened source, generation, stream or timing identity.";
        if (!navigation.HasFrameIndex || reference.FramePts.Count == 0)
            return "Terminal qualification requires a complete decoded frame index and reference.";
        for (int i = 1; i < reference.FramePts.Count; i++)
            if (reference.FramePts[i] <= reference.FramePts[i - 1])
                return "The reference frame sequence is not strictly ordered.";
        if (!navigation.FramePts.SequenceEqual(reference.FramePts))
            return "The complete decoded frame sequence differs from the independent reference.";
        if (reference.EndPts <= reference.FramePts[^1] || navigation.TerminalEndPts != reference.EndPts)
            return "The proposed terminal end differs from the independently established presentation end.";
        return null;
    }
}
