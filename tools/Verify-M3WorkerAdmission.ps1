param([string]$SourcePath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$SourcePath) { $SourcePath = Join-Path $repo 'artifacts/m3-copy/iteration-3/h264.mp4' }
$output = Join-Path $repo ('artifacts/m54-workers/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$mediaProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'WorkerContract.csproj') -Encoding utf8
@'
using System.Security.Cryptography;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;

static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static bool WriteBlocked(string path)
{
    try { using var handle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return false; }
    catch (IOException) { return true; }
}
try
{
    string source = Path.Combine(args[2], "source.mp4"); File.Copy(args[0], source, overwrite:false);
    string original = Hash(source), directory = args[1], python = Path.Combine(directory, "python", "python.exe");
    var scene = new SceneWorker(python, Path.Combine(directory, "vidsc_scene_job.py"));
    var evidence = new InspectionEvidenceWorker(python, Path.Combine(directory, "vidsc_inspection_job.py"));
    var finite = new FiniteCopyVerifier(python, Path.Combine(directory, "vidsc_copy_job.py"));
    var navigation = new FfprobeNavigationReader(args[3]);
    var progressive = new FfprobeProgressiveReader(args[3]);
    var video = new MediaStreamInfo(0, "video", "h264", null, 64, 64, "yuv420p", 0,
        Rational.Create(1, 12288), null, null, null, null, null, null, null);
    var identity = InspectionSourceIdentity.Capture(source);
    var stream = new Vidsc.Core.Navigation.ProgressiveSourceIdentity(identity.IdentityKey, 54, 0, Rational.Create(1, 12288), 0);
    var request = new InspectionEvidenceRequest("evidence-string-id", stream, "h264", CandidatePts:12288);
    var copy = new FiniteCopyRequest(stream, "h264", 12, 23, 12288, 24576);
    var neighborhood = new ProgressiveInspectionRequest(stream, 12_500_000, 25_000_000);
    var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    bool guardsHeld = false;
    var held = Task.Run(() => scene.RunAsync(source, 0, 1, "content", default,
        processStarted: _ =>
        {
            guardsHeld = WriteBlocked(source) && WriteBlocked(Path.Combine(directory, "vidsc_scene_job.py")) && WriteBlocked(python);
            began.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Started barrier expired.");
        }));
    using var cancellation = new CancellationTokenSource();
    Task<InspectionEvidenceResult>? queuedEvidence = null;
    Task<QualifiedCopyRange>? queuedCopy = null;
    Task? queuedNavigation = null, queuedProgressive = null;
    try
    {
        await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int starts = 0;
        queuedEvidence = evidence.RunAsync(source, request, cancellation.Token, _ => Interlocked.Increment(ref starts));
        queuedCopy = finite.QualifyAsync(source, copy, Guid.NewGuid(), cancellation.Token, _ => Interlocked.Increment(ref starts));
        queuedNavigation = navigation.ReadAsync(source, video, 0, true, cancellation.Token, _ => Interlocked.Increment(ref starts));
        queuedProgressive = progressive.ReadAsync(source, neighborhood, cancellation.Token, _ => Interlocked.Increment(ref starts));
        Require(!queuedEvidence.IsCompleted && !queuedCopy.IsCompleted && !queuedNavigation.IsCompleted && !queuedProgressive.IsCompleted,
            "Workers bypassed shared admission.");
        cancellation.Cancel();
        foreach (Task task in new Task[] { queuedEvidence, queuedCopy, queuedNavigation, queuedProgressive })
        {
            try { await task; throw new Exception("Queued worker ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        Require(starts == 0 && guardsHeld, "Queued process or unprotected worker.");
    }
    finally
    {
        cancellation.Cancel(); release.Set();
        await held;
        foreach (Task? task in new Task?[] { queuedEvidence, queuedCopy, queuedNavigation, queuedProgressive })
            if (task is not null) try { await task; } catch (OperationCanceledException) { }
    }
    var scenes = await held;
    Require(scenes.Success && scenes.Process is { ExitConfirmed:true } && scenes.RetainedJobId is null,
        "Actual scene worker failed: " + scenes.Error);
    bool evidenceGuards = false;
    var observed = await evidence.RunAsync(source, request, default, _ =>
        evidenceGuards = WriteBlocked(source) && WriteBlocked(Path.Combine(directory, "vidsc_inspection_job.py")));
    Require(observed.Success && observed.Reconstruction is not null && evidenceGuards, "Actual evidence worker failed: " + observed.Error);
    using var stop = new CancellationTokenSource();
    var cancelledScene = await scene.RunAsync(source, 0, 1, "content", stop.Token, processStarted:_ => stop.Cancel());
    Require(!cancelledScene.Success && cancelledScene.Process is { Cancelled:true, ExitConfirmed:true }
        && cancelledScene.RetainedJobId is null, "Started scene cancellation did not confirm exit.");
    using var stopEvidence = new CancellationTokenSource();
    bool evidenceCancelled = false;
    try { await evidence.RunAsync(source, request, stopEvidence.Token, _ => stopEvidence.Cancel()); }
    catch (OperationCanceledException) { evidenceCancelled = true; }
    Require(evidenceCancelled, "Evidence cancellation was not observed.");
    bool probeGuards = true;
    void ProbeStarted(ToolProcessIdentity _) => probeGuards &= WriteBlocked(source) && WriteBlocked(args[3]);
    var map = await navigation.ReadAsync(source, video, 0, true, default, ProbeStarted);
    var snapshot = await progressive.ReadAsync(source, neighborhood, default, ProbeStarted);
    Require(map.FramePts.Count == 48 && snapshot.HasRequestedDecodedContext && probeGuards,
        "Actual streaming probes failed or released guards early.");
    foreach (bool useProgressive in new[] { false, true })
    {
        using var stopProbe = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            if (useProgressive) await progressive.ReadAsync(source, neighborhood, stopProbe.Token, _ => stopProbe.Cancel());
            else await navigation.ReadAsync(source, video, 0, true, stopProbe.Token, _ => stopProbe.Cancel());
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Started streaming probe ignored cancellation.");
    }
    var qualified = await finite.QualifyAsync(source, copy, Guid.NewGuid(), default);
    Require(qualified.ManifestID.Length == 64 && Hash(source) == original, "Copy peer reuse or source preservation failed.");
    Console.WriteLine("PASS actual packaged scene/evidence/copy/navigation/progressive admission, queued and started cancellation, guards, reuse and source preservation.");
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'WorkerContract.csproj') --configuration Release -- `
    ([IO.Path]::GetFullPath($SourcePath)) (Join-Path $repo 'artifacts/release/app/scene') $output `
    (Join-Path $repo 'artifacts/release/app/tools/ffmpeg/ffprobe.exe')
if ($LASTEXITCODE) { throw 'M3 actual worker admission contract failed.' }
Write-Host "PASS M3 worker admission. Owned artifacts: $output"
