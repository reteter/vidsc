using System.Text.Json;
using System.Text.Json.Serialization;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;

namespace Vidsc.Core.Projects;

public sealed record ProjectSource(string Path, string Identity, int VideoStream, long TimelineOriginTicks);
public sealed record EditorSegment(Guid Id, string Name, RationalTime RequestedStart, RationalTime RequestedEnd,
    ResolvedFrameInterval? Resolved = null);
public enum ExportMode { StreamCopy, AccurateVideo }
public sealed record EditorSettings(CopyContainer Container, CopyBoundaryPolicy BoundaryPolicy,
    int[] SelectedTracks, bool PreserveChapters, string Destination)
{
    public const string ReferenceQualityPolicy = "accurate-cfr-reference-crf18-slow-v1";
    public ExportMode Mode { get; init; } = ExportMode.StreamCopy;
    public string QualityPolicy { get; init; } = ReferenceQualityPolicy;
    public bool AllowFullSelectedEncode { get; init; }
}
public sealed record TimelineViewport(double Start, double Span);
// Raw draft text may be incomplete or invalid while the accepted segment list remains saveable.
public sealed record EditorDraft(string Name, string StartText, string EndText,
    RationalTime? ExactSourceStart, RationalTime? ExactSourceEnd, Guid? SelectedSegmentId);

/// <summary>Saved edit intent. Runtime qualification and presentation authority are deliberately absent.</summary>
public sealed record EditorProject(int Version, ProjectSource Source, EditorSegment[] Segments,
    EditorSettings Settings, TimelineViewport Viewport)
{
    public const int CurrentVersion = 2;
    public EditorDraft? Draft { get; init; }
}

public static class ProjectJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, IgnoreReadOnlyProperties = true };
        options.Converters.Add(new RationalConverter());
        return options;
    }

    public static string Serialize(EditorProject project) => JsonSerializer.Serialize(project, Options);
    public static EditorProject Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<EditorProject>(json, Options)
            ?? throw new InvalidDataException("Empty editor project.");
        if (project.Version is not (1 or EditorProject.CurrentVersion)) throw new InvalidDataException("Unsupported editor project version.");
        if (project.Version == 1 && project.Settings is not null)
            project = project with { Version = EditorProject.CurrentVersion,
                Settings = project.Settings with { Mode = ExportMode.StreamCopy, QualityPolicy = EditorSettings.ReferenceQualityPolicy, AllowFullSelectedEncode = false } };
        if (project.Source is null || string.IsNullOrWhiteSpace(project.Source.Path) || string.IsNullOrWhiteSpace(project.Source.Identity)
            || project.Source.VideoStream < 0 || project.Settings is null || project.Settings.SelectedTracks is null
            || !Enum.IsDefined(project.Settings.Container) || !Enum.IsDefined(project.Settings.BoundaryPolicy)
            || !Enum.IsDefined(project.Settings.Mode) || project.Settings.QualityPolicy != EditorSettings.ReferenceQualityPolicy
            || project.Settings.SelectedTracks.Length > 10000 || project.Settings.SelectedTracks.Any(t => t < 0)
            || project.Segments is null || project.Segments.Length > 10000 || project.Segments.Any(s => !ValidSegment(s))
            || (project.Draft is { } draft && (draft.Name is null || draft.Name.Length > 4096
                || draft.StartText is null || draft.StartText.Length > 4096 || draft.EndText is null || draft.EndText.Length > 4096
                || (draft.ExactSourceStart is { } ds && !ds.TimeBase.IsPositive)
                || (draft.ExactSourceEnd is { } de && !de.TimeBase.IsPositive)))
            || project.Segments.Select(s => s.Id).Distinct().Count() != project.Segments.Length
            || project.Viewport is null || !double.IsFinite(project.Viewport.Start) || project.Viewport.Start < 0
            || !double.IsFinite(project.Viewport.Span) || project.Viewport.Span <= 0)
            throw new InvalidDataException("Invalid editor project data.");
        return project;
    }

    private static bool ValidSegment(EditorSegment? s) => s is not null && s.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(s.Name) && s.Name.Length <= 4096 && s.RequestedStart.TimeBase.IsPositive
        && s.RequestedEnd.TimeBase.IsPositive && s.RequestedStart < s.RequestedEnd
        && (s.Resolved is null || (s.Resolved.Start is not null && s.Resolved.End is not null
            && s.Resolved.Start.Requested.TimeBase.IsPositive && s.Resolved.End.Requested.TimeBase.IsPositive
            && s.Resolved.Start.Resolved.TimeBase.IsPositive && s.Resolved.End.Resolved.TimeBase.IsPositive
            && s.Resolved.Start.Resolved < s.Resolved.End.Resolved));

    private sealed class RationalConverter : JsonConverter<Rational>
    {
        public override Rational Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            return Rational.Create(value.RootElement.GetProperty("Numerator").GetInt64(), value.RootElement.GetProperty("Denominator").GetInt64());
        }
        public override void Write(Utf8JsonWriter writer, Rational value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteNumber("Numerator", value.Numerator); writer.WriteNumber("Denominator", value.Denominator); writer.WriteEndObject();
        }
    }
}
