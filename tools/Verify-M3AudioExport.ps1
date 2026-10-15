param(
    [string]$PythonPath,
    [string]$DriverPath,
    [string]$SourcePath,
    [string]$FfmpegPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/release/app/scene/python/python.exe' }
if (!$DriverPath) { $DriverPath = Join-Path $repo 'artifacts/release/app/scene/vidsc_audio_copy_job.py' }
if (!$SourcePath) { $SourcePath = Join-Path $repo 'artifacts/m3-tracks/authored-edges-2/source.mp4' }
if (!$FfmpegPath) { $FfmpegPath = Join-Path $repo 'artifacts/release/app/tools/ffmpeg/ffmpeg.exe' }
$output = Join-Path $repo ('artifacts/m43-export/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$mediaProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'AudioContract.csproj') -Encoding utf8
@'
using System.Security.Cryptography;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
try
{
    var python = args[0]; var driver = args[1]; var work = args[4];
    var sourcePath = Path.Combine(work, "source.mp4");
    File.Copy(args[2], sourcePath, overwrite:false);
    string sourceHash = Hash(sourcePath);
    var identity = InspectionSourceIdentity.Capture(sourcePath);
    var tb = Rational.Create(1, 12288);
    var source = new ProgressiveSourceIdentity(identity.IdentityKey, 43, 0, tb, 0);
    var request = new FiniteCopyRequest(source, "h264", 12, 23, 12288, 24576);
    var videoVerifier = new FiniteCopyVerifier(python, Path.Combine(Path.GetDirectoryName(driver)!, "vidsc_copy_job.py"));
    var audioVerifier = new PacketAudioCopyVerifier(python, driver);
    var video = await videoVerifier.QualifyAsync(sourcePath, request, Guid.NewGuid(), default);
    var audio = await audioVerifier.QualifyAsync(sourcePath, video, [new(1, "aac")], Guid.NewGuid(), default);
    bool ac3Refused = false;
    try { await audioVerifier.QualifyAsync(sourcePath, video, [new(2, "ac3")], Guid.NewGuid(), default); }
    catch (InvalidOperationException) { ac3Refused = true; }
    Require(ac3Refused, "AC-3 context must be explicitly refused.");
    var bounds = new ResolvedFrameInterval(new(new(12288, tb), new(12288, tb)), new(new(24576, tb), new(24576, tb)));
    Require(CopyBoundaryPlanner.TryPropose(video.ToObservation().Binding, bounds,
        CopyBoundaryPolicy.Expand, [video.ToObservation()], out var proposal), "Qualified video did not reach planner.");
    var exporter = new VerifiedCopyExporter(videoVerifier, new FfmpegCli(args[3], args[3]), audioVerifier);
    // Hold a real qualification at its started callback while independent callers queue.
    // This barrier controls ownership timing without guessing a native job duration.
    var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    var heldQualification = Task.Run(() => audioVerifier.QualifyAsync(sourcePath, video, [new(1, "aac")],
        Guid.NewGuid(), default, _ =>
        {
            began.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Admission barrier expired.");
        }));
    using var queuedCancellation = new CancellationTokenSource();
    Task<QualifiedAudioCopyRange>? queuedAudio = null;
    Task<QualifiedCopyRange>? queuedFinite = null;
    Task<VerifiedCopyExportResult>? queuedVideo = null;
    try
    {
        await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int queuedStarts = 0;
        var otherVerifier = new PacketAudioCopyVerifier(python, driver);
        queuedAudio = otherVerifier.QualifyAsync(sourcePath, video, [new(1, "aac")], Guid.NewGuid(),
            queuedCancellation.Token, _ => Interlocked.Increment(ref queuedStarts));
        queuedFinite = new FiniteCopyVerifier(python, Path.Combine(Path.GetDirectoryName(driver)!, "vidsc_copy_job.py"))
            .QualifyAsync(sourcePath, request, Guid.NewGuid(), queuedCancellation.Token,
                _ => Interlocked.Increment(ref queuedStarts));
        queuedVideo = new VerifiedCopyExporter(videoVerifier, new FfmpegCli(args[3], args[3]))
            .ExportVideoOnlyAsync(sourcePath, video, proposal!, Path.Combine(work, "queued.mp4"),
                Path.Combine(work, "queued-journals"), queuedCancellation.Token,
                _ => Interlocked.Increment(ref queuedStarts));
        Require(!queuedAudio.IsCompleted && !queuedFinite.IsCompleted && !queuedVideo.IsCompleted,
            "Independent callers bypassed admission.");
        queuedCancellation.Cancel();
        try { await queuedAudio; throw new Exception("Queued audio ignored cancellation."); }
        catch (OperationCanceledException) { }
        try { await queuedFinite; throw new Exception("Queued finite video ignored cancellation."); }
        catch (OperationCanceledException) { }
        try { await queuedVideo; throw new Exception("Queued video ignored cancellation."); }
        catch (OperationCanceledException) { }
        Require(queuedStarts == 0 && !Directory.Exists(Path.Combine(work, "queued-journals")),
            "A queued caller started a process or transaction.");
    }
    finally
    {
        queuedCancellation.Cancel();
        release.Set();
        await heldQualification;
        foreach (Task? pending in new Task?[] { queuedAudio, queuedFinite, queuedVideo })
        {
            if (pending is null) continue;
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }
    var journals = Path.Combine(work, "journals");
    var destination = Path.Combine(work, "audio-result.mp4");
    var result = await exporter.ExportAudioAsync(sourcePath, audio, proposal!, destination, journals, default);
    Require(result.Success && result.ExitConfirmed && File.Exists(destination), "Audio promotion failed: " + result.Error);
    Require(result.PresentationEnd!.Value.AsSeconds() == Rational.FromInt64(1), "Audio route video end differs.");
    Require(result.AudioTimings is { Count: 1 } && result.AudioTimings[0].StreamIndex == 1 &&
        result.AudioTimings[0].OutputAudibleInterval == new CopyTimeInterval(Rational.Zero, Rational.FromInt64(1)),
        "Actual audio timing disclosure is missing.");
    Require(OwnedOutputTransaction.Recover(result.JournalPath!) == OwnedOutputRecoveryStatus.Recorded, "Recovery failed.");
    string preserved = Hash(destination);
    var conflict = await exporter.ExportAudioAsync(sourcePath, audio, proposal!, destination, journals, default);
    Require(!conflict.Success && Hash(destination) == preserved, "Existing destination changed.");
    using var cancellation = new CancellationTokenSource();
    int starts = 0;
    var cancelledPath = Path.Combine(work, "cancelled.mp4");
    var cancelled = await exporter.ExportAudioAsync(sourcePath, audio, proposal!, cancelledPath, journals,
        cancellation.Token, processStarted:_ => { Interlocked.Increment(ref starts); cancellation.Cancel(); });
    Require(starts > 0 && cancelled.Cancelled && cancelled.ExitConfirmed && !File.Exists(cancelledPath),
        "Started cancellation did not preserve ownership: " + cancelled.Error);
    // An independent actual validator invocation must reject an invalid physical output.
    var damaged = Path.Combine(work, "damaged.mp4");
    File.WriteAllBytes(damaged, "invalid mp4"u8.ToArray());
    var job = Guid.NewGuid();
    string[] checkArgs = [driver, "--phase", "validate", "--input", sourcePath, "--job-id", job.ToString("D"),
        "--source-id", source.SourceId, "--generation", "43", "--stream", "0", "--codec", "h264",
        "--first-packet", "12", "--last-packet", "23", "--start-pts", "12288", "--end-pts", "24576",
        "--audio-stream", "1:aac", "--manifest-id", audio.ManifestID, "--output-media", damaged];
    var invalid = await ToolProcessRunner.RunAsync(python, checkArgs, TimeSpan.FromSeconds(40), default);
    Require(invalid.ExitConfirmed && !invalid.Succeeded, "Invalid physical output was accepted.");
    var afterCancellation = await exporter.ExportAudioAsync(sourcePath, audio, proposal!,
        Path.Combine(work, "after-cancellation.mp4"), journals, default);
    Require(afterCancellation.Success, "Audio slot or guard leaked after cancellation: " + afterCancellation.Error);
    var videoOnly = await exporter.ExportVideoOnlyAsync(sourcePath, video, proposal!,
        Path.Combine(work, "video-only.mp4"), journals, default);
    Require(videoOnly.Success, "Shared executor regressed the video-only route: " + videoOnly.Error);
    Require(Hash(sourcePath) == sourceHash, "Source changed.");
    Console.WriteLine("PASS actual shared admission across independent audio/video callers, queued cancellation, AAC qualification/copy/validation/promotion, conflict, started cancellation, invalid output, slot reuse and video-only coexistence.");
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'AudioContract.csproj') --configuration Release -- `
    ([IO.Path]::GetFullPath($PythonPath)) ([IO.Path]::GetFullPath($DriverPath)) `
    ([IO.Path]::GetFullPath($SourcePath)) ([IO.Path]::GetFullPath($FfmpegPath)) $output
if ($LASTEXITCODE) { throw 'M3 packet-aware audio integration failed.' }
Write-Host "PASS M3 audio integration. Owned artifacts: $output"
