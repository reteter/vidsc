param([string]$FfmpegDirectory = 'C:\ffmpeg\bin')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ('artifacts/progressive-contract/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $output | Out-Null
$ffmpeg = Join-Path $FfmpegDirectory 'ffmpeg.exe'
$ffprobe = Join-Path $FfmpegDirectory 'ffprobe.exe'
$manifest = Get-Content (Join-Path $repo 'tools/runtime-manifest.json') -Raw | ConvertFrom-Json
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $expected = $manifest.cli.observedSha256.$name
    if ($expected -and (Get-FileHash (Join-Path $FfmpegDirectory $name)).Hash -ine $expected) { throw "Pinned executable hash mismatch: $name" }
}
$fixture = Join-Path $output 'fixture.mp4'
& $ffmpeg -v error -f lavfi -i 'testsrc2=size=64x64:rate=12:duration=4' -c:v libx264 -threads 2 -g 12 -bf 2 -y $fixture
if ($LASTEXITCODE) { throw 'Synthetic fixture generation failed.' }
$hevc = Join-Path $output 'fixture-hevc.mp4'
& $ffmpeg -v error -f lavfi -i 'testsrc2=size=64x64:rate=12:duration=4' -c:v libx265 -pix_fmt yuv420p10le -threads 2 -x265-params 'pools=2:frame-threads=2:keyint=12:min-keyint=12:scenecut=0:open-gop=1:log-level=error' -y $hevc
if ($LASTEXITCODE) { throw 'Synthetic HEVC fixture generation failed.' }
$project = Join-Path $output 'ProgressiveContract.csproj'
$mediaProject = Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8
@'
using System.Diagnostics;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
try {
    var source = new ProgressiveSourceIdentity("synthetic", 1, 0, Rational.Create(1,12288), 0);
    var reader = new FfprobeProgressiveReader(args[0]);
    var first = await reader.ReadAsync(args[1], new(source,12_500_000,25_000_000), default);
    var second = await reader.ReadAsync(args[1], new(source,20_000_000,35_000_000), default);
    async Task<string[]> Reference(bool frames) {
        var start = new ProcessStartInfo(args[0]) { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true };
        foreach(var arg in new[]{"-v","error","-threads","2","-select_streams","0",frames?"-show_frames":"-show_packets","-show_entries",frames?"frame=pts,duration,pkt_pos":"packet=pts,dts,pos,flags,size","-of","compact=p=1:nk=0","-i",args[1]}) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if(process.ExitCode!=0 || !string.IsNullOrWhiteSpace(await errors)) throw new Exception("Exhaustive reference failed");
        return (await output).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>s.TrimEnd('\r')).ToArray();
    }
    var packets = (await Reference(false)).Select(FfprobeProgressiveReader.ParsePacket).Where(p=>p is not null).ToArray();
    var frames = (await Reference(true)).Select(FfprobeProgressiveReader.ParseFrame).Where(f=>f is not null).ToArray();
    bool FrameOracle(InspectionTraversal traversal, IEnumerable<FrameObservation> observed) {
        if(traversal.DecodedCoverage is not {} coverage) return false;
        var expected=frames.Where(f=>f!.Pts>=coverage.FirstPts && f.Pts<=coverage.LastPts);
        return observed.SequenceEqual(expected);
    }
    var merged = first.Merge(second);
    if (!merged.IsComplete || !first.HasRequestedDecodedContext || !second.HasRequestedDecodedContext) throw new Exception("Requested decoded context was not established");
    foreach(var traversal in merged.Traversals) {
        if (!FrameOracle(traversal,traversal.Frames)) throw new Exception("Decoded coverage differs from exhaustive sequence");
        var expectedPackets=packets.SkipWhile(p=>p!=traversal.Packets[0]).Take(traversal.Packets.Count);
        if(!traversal.Packets.SequenceEqual(expectedPackets)) throw new Exception("Packet traversal differs from exhaustive sequence");
    }
    var original=first.Traversals[0];
    if(FrameOracle(original,original.Frames.Where((_,i)=>i!=2))) throw new Exception("Oracle accepted a missing interior frame");
    var eof = await reader.ReadAsync(args[1],new(source,35_000_000,40_000_000),default);
    if(eof.HasRequestedDecodedContext || eof.Traversals.Count>3 || eof.Traversals[^1].MissingRequestedContext is null) throw new Exception("Unknown EOF context was certified");
    if (first.Traversals[0].PacketLandingPts != 12288) throw new Exception("Early seek landing was lost");
    var payloadReader = new FfprobeAccessPointReader(args[0]);
    var avc = await payloadReader.ReadAsync(args[1],0,1.25,default);
    if(!avc.Any(p=>p.Evidence.Kind==AccessPointKind.AvcIdr) || avc.Any(p=>p.Evidence.IsSafeDecodeStart || p.Evidence.IsSafeSplice)) throw new Exception("AVC payload classification failed");
    var hevc = await payloadReader.ReadAsync(args[2],0,1.25,default,maxPackets:16);
    if(!hevc.Any(p=>p.Evidence.Kind==AccessPointKind.HevcCra) || !hevc.Any(p=>p.Evidence.LeadingPictures.HasFlag(LeadingPictureKind.Rasl)) || hevc.Any(p=>p.Evidence.IsSafeDecodeStart || p.Evidence.IsSafeSplice)) throw new Exception("HEVC CRA/leading payload classification failed");
    using var cancel = new CancellationTokenSource();
    int processId=0;
    try { await reader.ReadAsync(args[1],new(source,0,40_000_000),cancel.Token,p=>{processId=p.ProcessId;cancel.Cancel();}); throw new Exception("Cancelled read returned evidence"); }
    catch(OperationCanceledException) { }
    try { using var owned=Process.GetProcessById(processId); if(!owned.HasExited) throw new Exception("Cancelled process remains alive"); } catch(ArgumentException) { }
    Console.WriteLine($"PASS referencePackets={packets.Length} referenceFrames={frames.Length} firstPackets={first.Packets.Count} secondPackets={second.Packets.Count} mergedPackets={merged.Packets.Count} landingPts={first.Traversals[0].PacketLandingPts} exactCoverage=matched mutatedMissingFrame=rejected eofContext=unknown AVC=IDR HEVC=CRA+RASL safeSplice=unproven cancellation=confirmed");
} catch(Exception error) { Console.Error.WriteLine(error); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project $project --configuration Release -- $ffprobe $fixture $hevc
if ($LASTEXITCODE) { throw 'Progressive C# contract failed.' }
