param(
    [string]$PythonPath,
    [string]$FfmpegPath = 'C:\ffmpeg\bin\ffmpeg.exe',
    [string]$FfprobePath = 'C:\ffmpeg\bin\ffprobe.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/runtime/scene/python/python.exe' }
$PythonPath = [IO.Path]::GetFullPath($PythonPath)
$driver = Join-Path $repo 'tools/scene/vidsc_inspection_job.py'
$output = Join-Path $repo ('artifacts/m2-evidence/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
& $PythonPath (Join-Path $repo 'tools/scene/test_m2_evidence.py') --driver $driver --ffmpeg $FfmpegPath --work-dir $output
if ($LASTEXITCODE) { throw 'Independent reconstruction native contract failed.' }
& $PythonPath (Join-Path $repo 'tools/scene/test_m2_terminal.py') --ffprobe $FfprobePath --work-dir $output
if ($LASTEXITCODE) { throw 'Authored terminal-end native contract failed.' }
$mediaProject = [System.Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'EvidenceContract.csproj') -Encoding utf8
@'
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static MediaStreamInfo Video(string codec, Rational tb) => new(
    Index:0, CodecType:"video", CodecName:codec, Profile:null, Width:64, Height:64,
    PixelFormat:codec == "hevc" ? "yuv420p10le" : "yuv420p", RotationDegrees:0,
    TimeBase:tb, AverageFrameRate:null, RealFrameRate:null, DurationTicks:null,
    Channels:null, SampleRate:null, ChannelLayout:null, Language:null);
static void CheckTerminal(ProgressiveSourceIdentity source, NavigationMap map,
    long[] authoredPts, long authoredEnd, string id)
{
    Require(map.FramePts.SequenceEqual(authoredPts), id + ": complete authored PTS differs");
    Require(map.TerminalEndPts == authoredEnd, id + ": measured terminal metadata differs");
    Require(!TerminalEndQualification.TryQualify(source, map, null, out _, out _), id + ": missing reference accepted");
    var reference = new IndependentTerminalReference(source, id,
        "authored-fixture-schedule-and-final-duration-before-measurement", authoredPts, authoredEnd);
    Require(TerminalEndQualification.TryQualify(source, map, reference, out var qualified, out var reason),
        id + ": qualification failed: " + reason);
    var plain = new PresentationBoundaryMap(source.Generation, source.StreamIndex, map);
    Require(!plain.TryResolve(source.Generation, source.StreamIndex, authoredPts[^1], out _), id + ": unqualified EOF resolved");
    var boundary = new PresentationBoundaryMap(source, map, qualified!);
    Require(boundary.TerminalEndVerified && boundary.TryResolve(source.Generation, source.StreamIndex,
        authoredPts[^1], out var end) && end == authoredEnd, id + ": qualified EOF boundary differs");
    var missing = new NavigationMap(map.TimeBase, map.TimelineOriginTicks, authoredPts, map.KeyframePts);
    Require(!TerminalEndQualification.TryQualify(source, missing, reference, out _, out _), id + ": missing end accepted");
    var forged = new NavigationMap(map.TimeBase, map.TimelineOriginTicks, authoredPts,
        map.KeyframePts, terminalEndPts:authoredEnd + 1);
    Require(!TerminalEndQualification.TryQualify(source, forged, reference, out _, out _), id + ": forged end accepted");
    var forgedReference = new IndependentTerminalReference(source, id + "-forged", "negative-control", authoredPts, authoredEnd + 1);
    Require(!TerminalEndQualification.TryQualify(source, map, forgedReference, out _, out _), id + ": forged oracle accepted");
    Console.WriteLine($"PASS terminal={id} frames={authoredPts.Length} end={authoredEnd} missing/forged=rejected typedBoundary=verified");
}
try
{
    var worker = new InspectionEvidenceWorker(args[0], args[1]);
    var probe = new FfprobeNavigationReader(Path.GetFullPath(args[2]), maxFrameRecords:512,
        maxKeyframeRecords:1024, timeout:TimeSpan.FromSeconds(30));
    // Authored before fixture creation/measurement; never derive reference values from a report.
    long[] cfrPts = Enumerable.Range(0, 48).Select(n => (long)n * 1024).ToArray();
    const long cfrEnd = 49152;
    foreach (var (codec, fixture) in new[] { ("h264", "h264"), ("hevc", "hevc"), ("hevc", "hevc-idr") })
    {
        var path = Path.Combine(args[3], fixture + ".mp4");
        var tb = Rational.Create(1, 12288);
        var source = new ProgressiveSourceIdentity(InspectionSourceIdentity.Capture(path).IdentityKey, 7, 0, tb, 0);
        var result = await worker.RunAsync(path, new("nonzero-rap-" + fixture, source, codec, CandidatePts:12288), default);
        Require(result.Success && result.Navigation is not null && result.Reconstruction is not null,
            codec + ": native adapter failed: " + result.Error);
        Require(result.Reconstruction!.FirstPts == 12288 && result.Reconstruction.FrameCount == 36 &&
            result.Reconstruction.CandidatePacket > 0, codec + ": wrong nonzero candidate reconstruction");
        CheckTerminal(source, result.Navigation!, cfrPts, cfrEnd, "authored-cfr-" + fixture);
        var independent = await probe.ReadAsync(path, Video(codec, tb), 0, true, default);
        Require(independent.FramePts.SequenceEqual(result.Navigation!.FramePts) &&
            independent.TerminalEndPts == result.Navigation.TerminalEndPts, codec + ": ffprobe/native map differs");
        Console.WriteLine($"PASS reconstruction={fixture} codec={codec} candidatePts=12288 frames=36 independentProbe=matched");
    }
    long[] vfrPts = [10000,10040,10120,10300,10340,10520,10600,10800];
    const long vfrEnd = 10920;
    var vfrPath = Path.Combine(args[3], "offset-vfr.mkv");
    var vfrTb = Rational.Create(1, 1000);
    var vfrSource = new ProgressiveSourceIdentity(InspectionSourceIdentity.Capture(vfrPath).IdentityKey,
        7, 0, vfrTb, 100000000);
    var vfrMap = await probe.ReadAsync(vfrPath, Video("ffv1", vfrTb), vfrSource.TimelineOriginTicks, true, default);
    CheckTerminal(vfrSource, vfrMap, vfrPts, vfrEnd, "authored-offset-vfr");
    var vfrReference = new IndependentTerminalReference(vfrSource, "authored-vfr-epoch",
        "authored-PTS-and-final-duration-before-measurement", vfrPts, vfrEnd);
    Require(TerminalEndQualification.TryQualify(vfrSource, vfrMap, vfrReference, out var vfrQualified, out _), "VFR epoch qualification failed");
    var vfrBoundary = new PresentationBoundaryMap(vfrSource, vfrMap, vfrQualified!);
    Require(vfrBoundary.ToUiTicks(vfrPts[0]) == 0 && vfrBoundary.ToUiTicks(vfrPts[^1]) == 8000000,
        "VFR authored common epoch differs");
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'EvidenceContract.csproj') --configuration Release -- $PythonPath $driver $FfprobePath $output
if ($LASTEXITCODE) { throw 'Independent evidence C# adapter contract failed.' }
Write-Host "PASS M2 evidence integration. Owned artifacts: $output"
