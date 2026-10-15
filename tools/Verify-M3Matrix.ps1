param(
    [string]$PythonPath,
    [string]$DriverDirectory,
    [string]$FfmpegPath,
    [string]$FfprobePath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/release/app/scene/python/python.exe' }
if (!$DriverDirectory) { $DriverDirectory = Join-Path $repo 'artifacts/release/app/scene' }
if (!$FfmpegPath) { $FfmpegPath = Join-Path $repo 'artifacts/release/app/tools/ffmpeg/ffmpeg.exe' }
if (!$FfprobePath) { $FfprobePath = Join-Path $repo 'artifacts/release/app/tools/ffmpeg/ffprobe.exe' }
foreach ($path in @($PythonPath, $FfmpegPath, $FfprobePath)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required explicit runtime file is missing: $path" }
}
foreach ($helper in @('vidsc_copy_job.py', 'vidsc_inspection_job.py', 'vidsc_audio_copy.py', 'vidsc_audio_copy_job.py', 'vidsc_copy_discovery_job.py', 'vidsc_matroska_copy_job.py')) {
    if (!(Test-Path -LiteralPath (Join-Path $DriverDirectory $helper))) { throw "Required helper is missing: $helper" }
}
$work = Join-Path $repo ('artifacts/m3-export/matrix-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
function New-Fixture {
    param([string]$Name, [string[]]$MediaArguments)
    & $FfmpegPath -hide_banner -nostdin -v error -n @MediaArguments (Join-Path $work $Name)
    if ($LASTEXITCODE) { throw "Synthetic fixture generation failed: $Name" }
}
& $FfmpegPath -version | Set-Content -LiteralPath (Join-Path $work 'cli-version.txt') -Encoding utf8
$signal = 'aevalsrc=0.1*sin(2*PI*440*t)|0.1*sin(2*PI*550*t)|0.1*sin(2*PI*660*t)|0.1*sin(2*PI*110*t)|0.1*sin(2*PI*770*t)|0.1*sin(2*PI*880*t):s=48000:d=100:c=5.1'
$long = Join-Path $work 'long-cfr.mp4'
$mixed = Join-Path $work 'long-mixed.mp4'
New-Fixture 'long-cfr.mp4' @('-f','lavfi','-i','testsrc2=size=64x64:rate=25:duration=100','-c:v','libx264','-threads','2','-g','25','-bf','0','-x264-params','keyint=25:min-keyint=25:scenecut=0:open-gop=0','-video_track_timescale','1000','-pix_fmt','yuv420p')
New-Fixture 'long-mixed.mp4' @('-i',$long,'-f','lavfi','-i',$signal,'-map','0:v:0','-map','1:a:0','-map','1:a:0','-c:v','copy','-c:a:0','aac','-b:a:0','384k','-c:a:1','ac3','-b:a:1','448k','-threads','2','-video_track_timescale','1000')
New-Fixture 'delayed-mixed.mp4' @('-i',$long,'-itsoffset','0.25','-i',$mixed,'-itsoffset','0.5','-i',$mixed,'-t','4','-map','0:v:0','-map','1:a:0','-map','2:a:1','-c','copy','-video_track_timescale','1000')
New-Fixture 'vfr-offset.mp4' @('-i',$long,'-i',$mixed,'-t','4','-map','0:v:0','-map','1:a:0','-map','1:a:1','-vf','select=not(eq(mod(n\,5)\,2))','-fps_mode','vfr','-c:v','libx264','-threads','2','-g','20','-bf','0','-x264-params','keyint=20:min-keyint=20:scenecut=0:open-gop=0','-c:a','copy','-output_ts_offset','5','-video_track_timescale','1000')
New-Fixture 'hevc-mixed.mp4' @('-f','lavfi','-i','testsrc2=size=64x64:rate=12:duration=4','-f','lavfi','-i',$signal.Replace('d=100','d=4'),'-map','0:v:0','-map','1:a:0','-map','1:a:0','-c:v','libx265','-pix_fmt','yuv420p10le','-x265-params','keyint=12:min-keyint=12:scenecut=0:open-gop=1:bframes=3:pools=none:frame-threads=2:log-level=error','-c:a:0','aac','-aac_pns','0','-b:a:0','384k','-c:a:1','ac3','-b:a:1','448k','-threads','2')
New-Fixture 'terminal-cfr.mp4' @('-i',$long,'-t','4','-map','0:v:0','-an','-c','copy','-video_track_timescale','1000')
@'
1
00:00:00,500 --> 00:00:01,500
First fixture cue

2
00:00:01,500 --> 00:00:02,500
Second fixture cue
'@ | Set-Content -LiteralPath (Join-Path $work 'subtitles.srt') -Encoding utf8
@'
;FFMETADATA1
title=Matroska fixture
[CHAPTER]
TIMEBASE=1/1000
START=500
END=1500
title=First fixture chapter
[CHAPTER]
TIMEBASE=1/1000
START=1500
END=2500
title=Second fixture chapter
'@ | Set-Content -LiteralPath (Join-Path $work 'chapters.txt') -Encoding utf8
New-Fixture 'subtitles-chapters.mkv' @('-f','lavfi','-i','testsrc2=size=64x64:rate=25:duration=3','-i',(Join-Path $work 'subtitles.srt'),'-i',(Join-Path $work 'chapters.txt'),'-map','0:v:0','-map','1:s:0','-map_metadata','2','-map_chapters','2','-c:v','libx264','-threads','2','-g','25','-bf','0','-x264-params','scenecut=0','-pix_fmt','yuv420p','-c:s','copy','-metadata:s:s:0','language=eng','-metadata:s:s:0','title=Fixture subtitles')
$mediaProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'))
$runnerSource = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'verification/M3Matrix.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject"/><Compile Include="$runnerSource"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $work 'M3Matrix.csproj') -Encoding utf8
& dotnet run --project (Join-Path $work 'M3Matrix.csproj') --configuration Release -- `
    ([IO.Path]::GetFullPath($PythonPath)) ([IO.Path]::GetFullPath($DriverDirectory)) `
    ([IO.Path]::GetFullPath($FfmpegPath)) ([IO.Path]::GetFullPath($FfprobePath)) $work (Join-Path $PSScriptRoot 'verification/m3_subtitle_negative.py')
if ($LASTEXITCODE) { throw 'M3 matrix failed. No gate completion is implied by a partial run.' }
Write-Host "PASS M3 bounded matrix. Managed project source with explicitly selected native/runtime helpers. Owned evidence: $work"
