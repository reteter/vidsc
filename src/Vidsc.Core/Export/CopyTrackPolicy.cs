using Vidsc.Core.Time;

namespace Vidsc.Core.Export;

public enum CopyTrackKind { Video, Audio, Subtitle }
public enum CopyContainer { Mp4, Matroska }
public enum CopySubtitleChoice { Reject, Sidecar }
public sealed record CopyTrack(int Id, CopyTrackKind Kind, string Codec, bool IsDefault,
    string? Language, IReadOnlyDictionary<string, string> Metadata);
public sealed record CopyAudioPacket(long Ordinal, RationalTime? PresentationStart, RationalTime? Duration);

/// <summary>Explicit codec observation; null means unknown, including unknown absence of preroll or skips.</summary>
public sealed record CopyAudioContext(string ConfigurationId, string Provenance,
    IReadOnlyList<long>? RequiredPrerollOrdinals, long? PrimingSamples, long? SkipStartSamples, long? SkipEndSamples);
public sealed record CopyTimeInterval(Rational Start, Rational End);
public sealed record CopyAudioSelection(CopyTrack Track, CopyAudioContext Context,
    IReadOnlyList<CopyAudioPacket> IntersectingPackets, IReadOnlyList<CopyAudioPacket> PrerollPackets,
    CopyTimeInterval IntendedAudibleInterval, CopyTimeInterval RetainedPresentationInterval,
    CopyTimeInterval OutputAudibleInterval, CopyTimeInterval OutputRetainedInterval,
    Rational LeadingExtension, Rational TrailingExtension, Rational SourceEpoch, Rational CommonShift);
public sealed record CopyTrackMapping(CopyTrack Track, bool IsSidecar);
public sealed record CopyChapter(string Id, MediaInterval Interval, string Label, IReadOnlyDictionary<string, string> Metadata);
public sealed record CopyChapterMapping(CopyChapter Source, CopyTimeInterval ClippedSourceInterval, CopyTimeInterval OutputInterval);

/// <summary>Draft mapping policies only. Caller observations do not certify native muxing or sample-exact audio.</summary>
public static class CopyTrackPolicy
{
    public static bool TrySelectAudio(MediaInterval video, CopyTrack track, IReadOnlyList<CopyAudioPacket> packets,
        CopyAudioContext context, Rational commonShift, out CopyAudioSelection? selection, out string? rejection)
    {
        selection = null;
        rejection = "Unsupported or incomplete copied-audio observation.";
        if (!Valid(video) || !Valid(track) || track.Kind != CopyTrackKind.Audio
            || track.Codec is not ("aac" or "ac3" or "eac3") || string.IsNullOrWhiteSpace(context.ConfigurationId)
            || string.IsNullOrWhiteSpace(context.Provenance) || context.RequiredPrerollOrdinals is null
            || context.PrimingSamples is null or < 0 || context.SkipStartSamples is null or < 0
            || context.SkipEndSamples is null or < 0) return false;

        var observed = new Dictionary<long, (CopyAudioPacket Packet, Rational Start, Rational End)>();
        foreach (var packet in packets)
        {
            if (packet.Ordinal < 0 || packet.PresentationStart is not { } start || packet.Duration is not { } duration
                || !start.TimeBase.IsPositive || !duration.TimeBase.IsPositive || !duration.AsSeconds().IsPositive
                || observed.ContainsKey(packet.Ordinal)) return false;
            observed.Add(packet.Ordinal, (packet, start.AsSeconds(), start.AsSeconds() + duration.AsSeconds()));
        }
        Rational s = video.Start.AsSeconds(), e = video.End.AsSeconds();
        var intersecting = observed.Values.Where(p => p.Start < e && p.End > s).OrderBy(p => p.Packet.Ordinal).ToArray();
        if (intersecting.Length == 0) { rejection = "No audio packet intersects the resolved video interval."; return false; }
        var preroll = new List<CopyAudioPacket>();
        var seen = new HashSet<long>();
        Rational firstStart = intersecting.Min(p => p.Start);
        foreach (long ordinal in context.RequiredPrerollOrdinals)
        {
            // Preroll is decode context, never a guessed audible margin or a duplicate intersecting packet.
            if (!seen.Add(ordinal) || !observed.TryGetValue(ordinal, out var packet)
                || packet.End > s || packet.Start >= firstStart) return false;
            preroll.Add(packet.Packet);
        }
        var retained = intersecting.Concat(preroll.Select(p => observed[p.Ordinal])).ToArray();
        Rational startRetained = retained.Min(p => p.Start), endRetained = retained.Max(p => p.End);
        var audible = new CopyTimeInterval(s, e);
        var retainedInterval = new CopyTimeInterval(startRetained, endRetained);
        selection = new(track, context, intersecting.Select(p => p.Packet).ToArray(), preroll.OrderBy(p => p.Ordinal).ToArray(),
            audible, retainedInterval, Translate(audible, s, commonShift), Translate(retainedInterval, s, commonShift),
            Max(Rational.Zero, s - startRetained), Max(Rational.Zero, endRetained - e), s, commonShift);
        rejection = null;
        return true;
    }

    public static bool TryPlanTracks(CopyContainer container, IReadOnlyList<CopyTrack> tracks,
        CopySubtitleChoice subRipChoice, IReadOnlySet<int> testedTimingTrackIds,
        out IReadOnlyList<CopyTrackMapping> mappings, out string? rejection)
    {
        mappings = Array.Empty<CopyTrackMapping>();
        rejection = "Unsupported track/container choice or missing tested timing policy.";
        if (!Enum.IsDefined(container) || !Enum.IsDefined(subRipChoice) || tracks.Count == 0
            || tracks.Any(t => !Valid(t)) || tracks.Select(t => t.Id).Distinct().Count() != tracks.Count) return false;
        var result = new List<CopyTrackMapping>();
        foreach (var track in tracks)
        {
            bool known = track.Kind switch
            {
                CopyTrackKind.Video => track.Codec is "h264" or "hevc" || container == CopyContainer.Matroska && track.Codec == "av1",
                CopyTrackKind.Audio => track.Codec is "aac" or "ac3" || container == CopyContainer.Matroska && track.Codec == "eac3",
                CopyTrackKind.Subtitle => track.Codec == "subrip",
                _ => false
            };
            if (!known) return false;
            bool sidecar = container == CopyContainer.Mp4 && track.Kind == CopyTrackKind.Subtitle;
            if (sidecar && subRipChoice != CopySubtitleChoice.Sidecar) return false;
            if (container == CopyContainer.Mp4 && !sidecar && !testedTimingTrackIds.Contains(track.Id)) return false;
            result.Add(new(track, sidecar));
        }
        mappings = result;
        rejection = null;
        return true;
    }

    public static IReadOnlyList<CopyChapterMapping> ClipChapters(MediaInterval video,
        IEnumerable<CopyChapter> chapters, Rational commonShift)
    {
        if (!Valid(video)) throw new ArgumentException("A nonempty video interval is required.", nameof(video));
        var result = new List<CopyChapterMapping>();
        Rational s = video.Start.AsSeconds(), e = video.End.AsSeconds();
        foreach (var chapter in chapters)
        {
            if (!Valid(chapter.Interval) || string.IsNullOrWhiteSpace(chapter.Id))
                throw new ArgumentException("Chapter timing and identity must be known.", nameof(chapters));
            Rational start = Max(s, chapter.Interval.Start.AsSeconds());
            Rational end = Min(e, chapter.Interval.End.AsSeconds());
            if (start >= end) continue;
            var clipped = new CopyTimeInterval(start, end);
            result.Add(new(chapter, clipped, Translate(clipped, s, commonShift)));
        }
        return result;
    }

    private static bool Valid(MediaInterval interval) => interval.Start.TimeBase.IsPositive
        && interval.End.TimeBase.IsPositive && !interval.IsEmpty;
    private static bool Valid(CopyTrack track) => track.Id >= 0 && Enum.IsDefined(track.Kind)
        && !string.IsNullOrWhiteSpace(track.Codec) && track.Metadata is not null;
    private static CopyTimeInterval Translate(CopyTimeInterval value, Rational epoch, Rational shift)
        => new(value.Start - epoch + shift, value.End - epoch + shift);
    private static Rational Min(Rational a, Rational b) => a < b ? a : b;
    private static Rational Max(Rational a, Rational b) => a > b ? a : b;
}
