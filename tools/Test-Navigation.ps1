#Requires -Version 7.0
<#
.SYNOPSIS
    Checks the packaged navigation reader against small deterministic media fixtures.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $projectRoot 'artifacts/release/app'
$ffmpeg = Join-Path $appRoot 'tools/ffmpeg/ffmpeg.exe'
$ffprobe = Join-Path $appRoot 'tools/ffmpeg/ffprobe.exe'
$runRoot = Join-Path $projectRoot ('artifacts/tests/navigation/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot -Force

foreach ($required in @($ffmpeg, $ffprobe, (Join-Path $appRoot 'Vidsc.Media.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Build the Release bundle first: missing $required" }
}
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $appRoot 'Vidsc.Core.dll'))
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $appRoot 'Vidsc.Media.dll'))

function Assert-Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in @(
    @{ Name = 'cfr'; Rate = '24'; Variable = $false; Offset = $false },
    @{ Name = 'fractional'; Rate = '24000/1001'; Variable = $false; Offset = $false },
    @{ Name = 'vfr-offset'; Rate = '24'; Variable = $true; Offset = $true }
)) {
    $source = Join-Path $runRoot ($case.Name + '.mp4')
    $arguments = @('-hide_banner', '-nostdin', '-v', 'error', '-n', '-f', 'lavfi',
        '-i', "testsrc2=size=160x90:rate=$($case.Rate)", '-frames:v', '48',
        '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
        '-g', '12', '-keyint_min', '12', '-sc_threshold', '0', '-bf', '2')
    if ($case.Variable) { $arguments += @('-vf', "setpts='if(lt(N,24),N,2*N-24)/(24*TB)'", '-fps_mode', 'vfr') }
    if ($case.Offset) { $arguments += @('-output_ts_offset', '2') }
    & $ffmpeg @arguments $source
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed: $($case.Name)" }
    $metadata = (& $ffprobe -v error -show_format -show_streams -of json -i $source) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Fixture metadata probe failed.' }
    $info = $null
    $parseError = $null
    Assert-Check ([Vidsc.Media.Inspection.FfprobeJsonParser]::TryParse($source, $metadata, [ref]$info, [ref]$parseError)) "Metadata failed: $parseError"
    $origin = if ($null -eq $info.ContainerStartTicks) { 0L } else { [long]$info.ContainerStartTicks }
    $reader = [Vidsc.Media.Inspection.FfprobeNavigationReader]::new($ffprobe)
    $map = $reader.ReadAsync($source, $info.PrimaryVideo, $origin, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert-Check $map.HasFrameIndex "$($case.Name): frame index unavailable: $($map.FrameIndexUnavailableReason)"
    Assert-Check ($map.FrameCount -eq 48) "$($case.Name): expected 48 frames, got $($map.FrameCount)"
    Assert-Check ($map.KeyframeTicks.Count -eq 4) "$($case.Name): expected four key packets"
    $maximumError = 0L
    for ($i = 0; $i -lt 48; $i++) {
        $units = if ($case.Variable -and $i -ge 24) { 2 * $i - 24 } else { $i }
        $seconds = if ($case.Rate -eq '24000/1001') { [decimal]$units * 1001 / 24000 } else { [decimal]$units / 24 }
        $expected = [long][Math]::Round(($seconds * 10000000), 0, [MidpointRounding]::AwayFromZero)
        $actual = $map.FrameTicksAt($i)
        $errorTicks = [Math]::Abs($actual - $expected)
        $maximumError = [Math]::Max($maximumError, $errorTicks)
        Assert-Check ($errorTicks -le 1) "$($case.Name): frame $i expected $expected ticks, got $actual"
        Assert-Check ($map.FrameIndexAtOrBefore($actual) -eq $i) "$($case.Name): frame index lookup mismatch at $i"
    }
    $keysOnly = $reader.ReadAsync($source, $info.PrimaryVideo, $origin, $false, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert-Check (-not $keysOnly.HasFrameIndex) 'Packet scan must not claim a frame index.'
    Assert-Check (($keysOnly.KeyframePts -join ',') -eq ($map.KeyframePts -join ',')) 'Packet candidate maps differ.'

    $limited = [Vidsc.Media.Inspection.FfprobeNavigationReader]::new($ffprobe, 4)
    $limitRejected = $false
    try { $null = $limited.ReadAsync($source, $info.PrimaryVideo, $origin, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
    catch { $limitRejected = $_.Exception.ToString().Contains('frame limit exceeded') }
    Assert-Check $limitRejected 'The configured frame limit must reject a complete map.'
    $results.Add([pscustomobject]@{ fixture = $case.Name; frames = $map.FrameCount; keyPackets = $map.KeyframeTicks.Count; originTicks = $origin; maxErrorTicks = $maximumError; frameLimitRejected = $limitRejected })
}

# Damage only a new fixture copy. Invalid NAL lengths must not produce an accepted reduced frame map.
$packetJson = (& $ffprobe -v error -select_streams v:0 -show_packets -show_entries packet=pos,size -of json -i $source) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Packet position probe failed.' }
$packet = ($packetJson | ConvertFrom-Json).packets[10]
$damagedPath = Join-Path $runRoot 'damaged.mp4'
$bytes = [IO.File]::ReadAllBytes($source)
$position = [int]$packet.pos
for ($byte = 0; $byte -lt 4; $byte++) { $bytes[$position + $byte] = 255 }
[IO.File]::WriteAllBytes($damagedPath, $bytes)
$damageRejected = $false
try { $null = $reader.ReadAsync($damagedPath, $info.PrimaryVideo, $origin, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
catch { $damageRejected = $_.Exception.ToString() -match 'ffprobe reported errors|ffprobe navigation failed' }
Assert-Check $damageRejected 'A damaged decode must not publish a complete frame map.'

$report = [pscustomobject]@{ generatedUtc = [DateTime]::UtcNow.ToString('O'); passed = $true; scope = 'File navigation only; no presented-frame or safe-cut claim'; ffprobe = $ffprobe; damagedSourceRejected = $damageRejected; fixtures = $results }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'report.json') -Encoding utf8
$results | Format-Table
Write-Host "Navigation checks passed. Report: $(Join-Path $runRoot 'report.json')"
