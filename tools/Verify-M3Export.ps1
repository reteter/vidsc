param(
    [string]$PythonPath,
    [string]$DriverPath,
    [string]$FfmpegPath = 'C:\ffmpeg\bin\ffmpeg.exe',
    [string]$SourceDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/runtime/scene/python/python.exe' }
if (!$DriverPath) { $DriverPath = Join-Path $repo 'tools/scene/vidsc_copy_job.py' }
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repo 'artifacts/m3-copy/iteration-3' }
$output = Join-Path $repo ('artifacts/m3-export/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$mediaProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'ExportContract.csproj') -Encoding utf8
@'
using System.Security.Cryptography;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
try
{
    var verifier = new FiniteCopyVerifier(args[0], args[1]);
    var cli = new FfmpegCli(args[2], args[2]); // The exporter does not invoke ffprobe.
    var exporter = new VerifiedCopyExporter(verifier, cli);
    var tb = Rational.Create(1, 12288);
    foreach (var codec in new[] { "h264", "hevc" })
    {
        var path = Path.Combine(args[4], codec + "-source.mp4");
        File.Copy(Path.Combine(args[3], codec + ".mp4"), path, overwrite:false);
        string original = Hash(path);
        var identity = InspectionSourceIdentity.Capture(path);
        var source = new ProgressiveSourceIdentity(identity.IdentityKey, 8, 0, tb, 0);
        var request = new FiniteCopyRequest(source, codec, codec == "h264" ? 12 : 9,
            codec == "h264" ? 23 : 24, 12288, codec == "h264" ? 24576 : 25600);
        var qualified = await verifier.QualifyAsync(path, request, Guid.NewGuid(), default);
        var boundary = new ResolvedFrameInterval(new(new(12288, tb), new(12288, tb)), new(new(24576, tb), new(24576, tb)));
        Require(CopyBoundaryPlanner.TryPropose(qualified.ToObservation().Binding, boundary,
            CopyBoundaryPolicy.Expand, [qualified.ToObservation()], out var proposal), "Actual qualification did not reach planner");
        var destination = Path.Combine(args[4], codec + "-result.mp4");
        var journals = Path.Combine(args[4], "journals");
        var progress = new CaptureProgress();
        var result = await exporter.ExportVideoOnlyAsync(path, qualified, proposal!, destination, journals, default,
            progress:progress);
        Require(result.Success && result.ExitConfirmed && File.Exists(destination), "Actual copy/validation/promotion failed: " + result.Error);
        Require(progress.Count > 0, "Actual FFmpeg progress was not observed");
        Require(result.PresentationEnd!.Value.AsSeconds() == Rational.Create(codec == "h264" ? 12 : 13, 12), "Wrong measured presentation end");
        Require(OwnedOutputTransaction.Recover(result.JournalPath!) == OwnedOutputRecoveryStatus.Recorded, "Recorded output recovery differs");
        string final = Hash(destination);
        var conflict = await exporter.ExportVideoOnlyAsync(path, qualified, proposal!, destination, journals, default);
        Require(!conflict.Success && Hash(destination) == final && Hash(path) == original, "Conflict changed source or completed output");
        var forged = proposal! with { Selected = proposal.Selected with { PacketManifestId = "forged" } };
        var invalid = await exporter.ExportVideoOnlyAsync(path, qualified, forged,
            Path.Combine(args[4], codec + "-forged.mp4"), journals, default);
        Require(!invalid.Success && invalid.JournalPath is null, "Transparent proposal forged an execution capability");

        using var cancel = new CancellationTokenSource();
        string cancelledPath = Path.Combine(args[4], codec + "-cancelled.mp4");
        var cancelled = await exporter.ExportVideoOnlyAsync(path, qualified, proposal!, cancelledPath,
            journals, cancel.Token, _ => cancel.Cancel());
        Require(cancelled.Cancelled && cancelled.ExitConfirmed && !File.Exists(cancelledPath), "Started cancellation promoted output");
        string latePath = Path.Combine(args[4], codec + "-late.mp4");
        byte[] existing = [9,8,7,6];
        var late = await exporter.ExportVideoOnlyAsync(path, qualified, proposal!, latePath, journals, default,
            child => { if (Path.GetFileName(child.ExecutablePath).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                File.WriteAllBytes(latePath, existing); });
        Require(!late.Success && File.ReadAllBytes(latePath).SequenceEqual(existing), "Late conflict clobbered destination");
        Require(OwnedOutputTransaction.Recover(late.JournalPath!) == OwnedOutputRecoveryStatus.RetainedValidated,
            "Late conflict lost validated owned temporary");
        Require(Hash(path) == original, "Source changed");
        Console.WriteLine($"PASS fixture={codec} actualQualification=planner copy=validated promoted=recorded conflict=preserved forged=refused cancellation=confirmed lateConflict=retained");
    }
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
return 0;
sealed class CaptureProgress : IProgress<CopyExportProgress>
{
    private int count;
    public int Count => Volatile.Read(ref count);
    public void Report(CopyExportProgress value) => Interlocked.Increment(ref count);
}
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'ExportContract.csproj') --configuration Release -- `
    ([IO.Path]::GetFullPath($PythonPath)) ([IO.Path]::GetFullPath($DriverPath)) `
    ([IO.Path]::GetFullPath($FfmpegPath)) ([IO.Path]::GetFullPath($SourceDirectory)) $output
if ($LASTEXITCODE) { throw 'M3 actual adapter/export transaction contract failed.' }
Write-Host "PASS M3 bounded exporter integration. Owned artifacts: $output"
