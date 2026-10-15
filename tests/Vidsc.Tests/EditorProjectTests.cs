using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class EditorProjectTests
{
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
