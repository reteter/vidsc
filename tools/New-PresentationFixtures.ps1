param([string]$ToolDirectory = "$PSScriptRoot/../artifacts/release/app/tools/ffmpeg", [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$output = Join-Path $root "artifacts/tests/presentation/$RunId"
if (Test-Path $output) { throw "Output already exists: $output" }
$null = New-Item -ItemType Directory -Path $output
$ffmpeg = [IO.Path]::GetFullPath("$ToolDirectory/ffmpeg.exe")
$ffprobe = [IO.Path]::GetFullPath("$ToolDirectory/ffprobe.exe")
function Invoke-Media([string]$Executable, [string[]]$Arguments) {
    $result = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($result -join "`n") }
    return ($result -join "`n")
}
$version = Invoke-Media $ffmpeg @('-version')
$encoders = Invoke-Media $ffmpeg @('-hide_banner', '-encoders')
$font = (Join-Path $env:WINDIR 'Fonts/arial.ttf').Replace('\','/').Replace(':','\:')
if (!(Test-Path (Join-Path $env:WINDIR 'Fonts/arial.ttf'))) { throw 'Windows Arial font is required for the visible frame label.' }
# Twelve 24x60 tiles encode the presentation index, least-significant bit first.
$filter = "geq=lum='if(between(X,16,303)*between(Y,20,79),if(mod(floor(N/pow(2,floor((X-16)/24))),2),235,16),64)':cb=128:cr=128,drawtext=fontfile='$font':text='FRAME %{n}':x=16:y=105:fontsize=32:fontcolor=white,settb=1/24000"
$definitions = @(
    @{ Name='cfr24'; Step=1000; Offset=0; Vfr=$false; Codec='libx264' },
    @{ Name='fractional24000-1001'; Step=1001; Offset=0; Vfr=$false; Codec='libx264' },
    @{ Name='irregular-offset'; Step=1000; Offset=120000; Vfr=$true; Codec='libx264' },
    @{ Name='hevc-main10'; Step=1001; Offset=0; Vfr=$false; Codec='libx265' }
)
$results = @()
foreach ($d in $definitions) {
    if ($encoders -notmatch "\b$($d.Codec)\b") { $results += @{ name=$d.Name; skipped="Packaged encoder $($d.Codec) unavailable" }; continue }
    $formula = if ($d.Vfr) { '120000+floor(N/3)*6000+if(eq(mod(N,3),0),0,if(eq(mod(N,3),1),1000,3000))' } else { "N*$($d.Step)" }
    $path = Join-Path $output "$($d.Name).mp4"
    $args = @('-hide_banner','-loglevel','error','-f','lavfi','-i',$(if ($d.Step -eq 1001) {'color=c=gray:s=320x180:r=24000/1001'} else {'color=c=gray:s=320x180:r=24'}),'-vf',"$filter,setpts='$formula'",'-frames:v','48','-an','-c:v',$d.Codec,'-bf','3','-g','24','-threads','2','-pix_fmt',$(if ($d.Codec -eq 'libx265') {'yuv420p10le'} else {'yuv420p'}),'-enc_time_base','1/24000','-fps_mode','passthrough','-video_track_timescale','24000','-avoid_negative_ts','disabled')
    if ($d.Codec -eq 'libx265') { $args += @('-x265-params','pools=1:frame-threads=1:log-level=error') }
    $null = Invoke-Media $ffmpeg ($args + $path)
    $probe = (Invoke-Media $ffprobe @('-v','error','-select_streams','v:0','-show_streams','-show_frames','-of','json',$path)) | ConvertFrom-Json
    $expected = @(0..47 | ForEach-Object {
        $n = $_
        $pts = if ($d.Vfr) { 120000 + [Math]::Floor($n/3)*6000 + @(0,1000,3000)[$n%3] } else { $n*$d.Step }
        $duration = if ($d.Vfr) { @(1000,2000,3000)[$n%3] } else { $d.Step }
        [ordered]@{ index=$n; pts=[long]$pts; duration=[long]$duration; end=[long]($pts+$duration); uiPts=[long]($pts-$d.Offset) }
    })
    if ($probe.streams[0].time_base -ne '1/24000') { throw 'Unexpected stream time base; expected independent 1/24000 grid.' }
    if ($probe.frames.Count -ne 48) { throw 'Probe frame count differs from oracle.' }
    for ($n=0; $n -lt 48; $n++) { if ([long]$probe.frames[$n].pts -ne $expected[$n].pts) { throw "PTS mismatch $($d.Name) frame $n" } }
    if ([long]$probe.frames[-1].duration -ne $expected[-1].duration) { throw 'Final presentation duration differs from mathematical oracle.' }
    $raw = Join-Path $output "$($d.Name).gray"
    $null = Invoke-Media $ffmpeg @('-v','error','-i',$path,'-map','0:v:0','-fps_mode','passthrough','-pix_fmt','gray','-f','rawvideo',$raw)
    $bytes = [IO.File]::ReadAllBytes($raw)
    function Read-Barcode([int]$Frame) {
        $identity = 0
        for ($bit=0; $bit -lt 12; $bit++) {
            $sum=0
            for ($y=35; $y -lt 65; $y++) { for ($x=22+$bit*24; $x -lt 34+$bit*24; $x++) { $sum += $bytes[$Frame*57600+$y*320+$x] } }
            $mean = $sum/360
            if ($mean -gt 70 -and $mean -lt 180) { throw "Ambiguous barcode tile $bit at $Frame" }
            if ($mean -ge 180) { $identity = $identity -bor (1 -shl $bit) }
        }
        return $identity
    }
    function Assert-Sequence([int[]]$Sequence) {
        if ($Sequence.Count -ne 48) { throw 'Decoded frame count mismatch (including EOF).' }
        for ($n=0; $n -lt 48; $n++) { if ($Sequence[$n] -ne $n) { throw "Decoded identity mismatch at $n" } }
    }
    if ($bytes.Length -ne 48*57600) { throw 'Raw decoded frame count mismatch.' }
    $sequence = @(0..47 | ForEach-Object { Read-Barcode $_ })
    Assert-Sequence $sequence
    $negativeChecks = 0
    $badSequences = [Collections.Generic.List[object]]::new()
    $badSequences.Add([int[]]$sequence[0..46])
    $badSequences.Add([int[]](@($sequence[0..46]) + @(46)))
    $badSequences.Add([int[]](@($sequence[0..20]) + @(999) + @($sequence[22..47])))
    foreach ($bad in $badSequences) {
        try { Assert-Sequence $bad } catch { $negativeChecks++; continue }
        throw 'Oracle accepted deliberately damaged sequence.'
    }
    Remove-Item -LiteralPath $raw
    $results += [ordered]@{ name=$d.Name; file=$path; codec=$d.Codec; pixelFormat=$probe.streams[0].pix_fmt; bFrames=$probe.streams[0].has_b_frames; frameCount=48; width=320; height=180; timeBase='1/24000'; sourceEpoch=$d.Offset; uiEpoch=0; ptsFormula=$formula; durationFormula=$(if ($d.Vfr) {'[1000,2000,3000][n%3]'} else {"$($d.Step)"}); frames=$expected; barcodeBits=12; barcodeX=16; barcodeY=20; tileWidth=24; tileHeight=60; oracle='packaged FFmpeg full gray decode; tile means; independent index sequence and mathematical PTS'; negativeChecks=$negativeChecks; status='passed' }
}
$manifest = [ordered]@{ generatedUtc=[DateTime]::UtcNow.ToString('o'); ffmpeg=$ffmpeg; ffprobe=$ffprobe; version=($version -split "`n")[0]; fixtures=$results }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
Write-Output "Validated presentation fixtures: $output"



