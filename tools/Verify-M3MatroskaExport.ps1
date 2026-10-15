param(
    [string]$PythonPath,
    [string]$DriverPath,
    [string]$SourcePath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/release/app/scene/python/python.exe' }
if (!$DriverPath) { $DriverPath = Join-Path $repo 'artifacts/release/app/scene/vidsc_matroska_copy_job.py' }
if (!$SourcePath) { $SourcePath = Join-Path $repo 'artifacts/workflow/mkv-subtitle-source.mkv' }
$output = Join-Path $repo ('artifacts/m3-export/matroska-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$mediaProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'MatroskaContract.csproj') -Encoding utf8
@'
using System.Security.Cryptography;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
try
{
    string sourcePath = args[2], work = args[3];
    string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
    var identity = InspectionSourceIdentity.Capture(sourcePath);
    var tb = Rational.Create(1, 1000);
    var request = new FiniteCopyRequest(new(identity.IdentityKey, 1, 0, tb, 0), "h264", 25, 49, 1000, 2000);
    var finite = new FiniteCopyVerifier(args[0], Path.Combine(Path.GetDirectoryName(args[1])!, "vidsc_copy_job.py"));
    var qualification = await finite.QualifyAsync(sourcePath, request, Guid.NewGuid(), default);
    var requested = new ResolvedFrameInterval(new(new(1000, tb), new(1000, tb)), new(new(2000, tb), new(2000, tb)));
    if (!CopyBoundaryPlanner.TryPropose(qualification.ToObservation().Binding, requested, CopyBoundaryPolicy.Expand,
        [qualification.ToObservation()], out var proposal)) throw new Exception("Planner refused qualified source.");
    var exporter = new VerifiedCopyExporter(finite, new FfmpegCli("unused", "unused"),
        matroskaVerifier: new MatroskaCopyVerifier(args[0], args[1]));
    var destination = Path.Combine(work, "result.mkv");
    var result = await exporter.ExportMatroskaVideoAsync(sourcePath, qualification, proposal!, new([1], true),
        destination, Path.Combine(work, "journals"), default);
    if (!result.Success || result.PresentationEnd?.AsSeconds() != Rational.FromInt64(1))
        throw new Exception("Matroska transaction failed: " + result.Error);
    string preserved = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
    var conflict = await exporter.ExportMatroskaVideoAsync(sourcePath, qualification, proposal!, new([1], true),
        destination, Path.Combine(work, "journals"), default);
    if (conflict.Success || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))) != preserved)
        throw new Exception("Late destination conflict changed validated output.");
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) != sourceHash) throw new Exception("Source changed.");
    Console.WriteLine("PASS actual C# native MKV/SubRip/clipped translated chapters, full video validation, owned promotion and conflict preservation.");
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'MatroskaContract.csproj') --configuration Release -- `
    ([IO.Path]::GetFullPath($PythonPath)) ([IO.Path]::GetFullPath($DriverPath)) `
    ([IO.Path]::GetFullPath($SourcePath)) $output
if ($LASTEXITCODE) { throw 'M3 Matroska integration failed.' }
Write-Host "PASS M3 Matroska integration. Owned artifacts: $output"
