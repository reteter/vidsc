using System.Reflection;
using System.Text.Json.Nodes;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

public sealed class SceneTimingTests
{
    private static readonly SceneJobRequest Request = new("job", "fingerprint", 3, 0, 10);

    private static JsonObject Report() => JsonNode.Parse("""
        {"schemaVersion":2,"success":true,"jobId":"job","source":"fixture.mkv",
         "sourceIdentity":"fingerprint","generation":3,"streamIndex":0,
         "sourceOriginSeconds":10,"analyzedStartSeconds":1,"analyzedDurationSeconds":2,
         "detector":"content","threshold":27,"minSceneLenFrames":15,
         "backend":"pyav","scaleColorPolicy":"native-size-bgr24","decoderThreads":2,
         "timeBaseNumerator":1,"timeBaseDenominator":1000,
         "positionOrigin":"native-pts-minus-explicit-origin","positionPrecision":"decoded-frame-pts",
         "build":{"driver":"hash","pyav":"16","scenedetect":"0.6"},
         "scenes":[{"index":0,"startSeconds":1.04,"endSeconds":1.121,
             "startPts":11040,"endPts":11121,"timeBaseNumerator":1,"timeBaseDenominator":1000,
             "endIsPresentationBoundary":false}]}
        """)!.AsObject();

    private static SceneJobResult Parse(JsonObject report, ToolRunResult? process = null) =>
        (SceneJobResult)typeof(SceneWorker).GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["fixture.mkv", report.ToJsonString(), process, Request, 1.0, 2.0, "content"])!;

    [Fact]
    public void VfrPositionsUseActualPtsAndExplicitNonzeroOrigin()
    {
        SceneJobResult result = Parse(Report());
        Assert.True(result.Success, result.Error);
        Assert.Equal(1.04, result.Scenes[0].StartSeconds);
        Assert.Equal(11040, result.Scenes[0].StartPts);
        Assert.False(result.Scenes[0].EndIsPresentationBoundary);
        Assert.NotNull(result.CacheIdentity);
    }

    [Theory]
    [InlineData("jobId", "stale")]
    [InlineData("sourceIdentity", "other")]
    [InlineData("backend", "other")]
    public void MismatchedReportsCannotSucceed(string field, string value)
    {
        JsonObject report = Report();
        report[field] = value;
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void InconsistentPtsCannotSucceed()
    {
        JsonObject report = Report();
        report["scenes"]![0]!["startSeconds"] = 1.05;
        Assert.False(Parse(report).Success);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public void FailedProcessCannotPromoteSuccessReport(int exitCode, bool timedOut, bool cancelled)
    {
        var run = new ToolRunResult("python", [], exitCode, "", "", timedOut, cancelled, TimeSpan.Zero)
            { ExitConfirmed = true };
        Assert.False(Parse(Report(), run).Success);
    }
}
