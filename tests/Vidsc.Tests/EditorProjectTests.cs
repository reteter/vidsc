using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class EditorProjectTests
{
    [Fact]
    public void VersionOneActuallyMigratesAbsentModeAndQualityToCopy()
    {
        const string oldJson = """
        { "Version":1, "Source":{"Path":"fixture.mp4","Identity":"fixture-id","VideoStream":0,"TimelineOriginTicks":0},
          "Segments":[],"Settings":{"Container":0,"BoundaryPolicy":1,"SelectedTracks":[1],"PreserveChapters":true,"Destination":"output.mp4"},
          "Viewport":{"Start":0,"Span":1} }
        """;
        var loaded = ProjectJson.Deserialize(oldJson);
        Assert.Equal(EditorProject.CurrentVersion, loaded.Version);
        Assert.Equal(ExportMode.StreamCopy, loaded.Settings.Mode);
        Assert.Equal((CopyBoundaryPolicy)1, loaded.Settings.BoundaryPolicy);
        Assert.Equal(EditorSettings.ReferenceQualityPolicy, loaded.Settings.QualityPolicy);
        Assert.False(loaded.Settings.AllowFullSelectedEncode);
    }

    [Fact]
    public void AccurateSettingsRoundTripAndUnknownPoliciesFailClosed()
    {
        var project = new EditorProject(EditorProject.CurrentVersion, new("fixture.mp4", "identity", 0, 0), [],
            new(CopyContainer.Mp4, CopyBoundaryPolicy.Expand, [1], false, "result.mp4")
            { Mode = ExportMode.AccurateVideo, AllowFullSelectedEncode = true }, new(0, 1));
        string json = ProjectJson.Serialize(project);
        var loaded = ProjectJson.Deserialize(json);
        Assert.Equal(ExportMode.AccurateVideo, loaded.Settings.Mode);
        Assert.True(loaded.Settings.AllowFullSelectedEncode);
        Assert.Contains("\"Version\": 2", json);
        Assert.Throws<InvalidDataException>(() => ProjectJson.Deserialize(json.Replace("\"Mode\": 1", "\"Mode\": 99")));
        Assert.Throws<InvalidDataException>(() => ProjectJson.Deserialize(json.Replace(EditorSettings.ReferenceQualityPolicy, "unqualified")));
    }

    [Fact]
    public void JsonRoundTripKeepsFractionalSourceTimeWithoutRuntimeAuthority()
    {
        var start = new RationalTime(1001, Rational.Create(1, 24000));
        var end = new RationalTime(2002, Rational.Create(1, 24000));
        var segment = new EditorSegment(Guid.NewGuid(), "Fractional", start, end,
            new(new(start, start), new(end, end)));
        var project = new EditorProject(1, new("fixture.mp4", "source-identity", 2, 700), [segment],
            new(CopyContainer.Mp4, CopyBoundaryPolicy.Expand, [3, 4], false, "result.mp4"), new(12, 500));
        string json = ProjectJson.Serialize(project);
        var loaded = ProjectJson.Deserialize(json);
        Assert.Equal(start, loaded.Segments[0].RequestedStart);
        Assert.Equal(end, loaded.Segments[0].Resolved!.End.Resolved);
        Assert.Equal(new[] { 3, 4 }, loaded.Settings.SelectedTracks);
        Assert.Equal(project.Viewport, loaded.Viewport);
        Assert.DoesNotContain("Generation", json);
        Assert.DoesNotContain("Qualification", json);
        Assert.Throws<InvalidDataException>(() => ProjectJson.Deserialize(json.Replace("\"Version\": 1", "\"Version\": 9")));
    }
}
