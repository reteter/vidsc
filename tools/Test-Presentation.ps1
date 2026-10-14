param(
    [string]$Manifest = "$PSScriptRoot/../artifacts/tests/presentation/m1-oracle-04/manifest.json",
    [string]$AppDirectory = "$PSScriptRoot/../artifacts/release/app",
    [string]$FixtureName,
    [switch]$ExhaustiveSteps,
    [switch]$Lifecycle,
    [switch]$BoundarySeeks,
    [string]$SecondFixtureName,
    [string]$RunId = ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
)
$ErrorActionPreference = 'Stop'
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$winapp = (Get-Command winapp.exe -CommandType Application -ErrorAction Stop).Source
$output = [IO.Path]::GetFullPath("$PSScriptRoot/../artifacts/tests/presentation-check/$RunId")
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
$null = New-Item -ItemType Directory -Path $output
$fixtures = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).fixtures
if ($Lifecycle) {
    if (!$FixtureName -or !$SecondFixtureName -or $FixtureName -eq $SecondFixtureName) { throw 'Lifecycle requires two distinct fixture names.' }
    $second = @($fixtures | Where-Object { $_.name -eq $SecondFixtureName -and $_.status -eq 'passed' })
    if ($second.Count -ne 1) { throw 'Replacement fixture must have a passed oracle.' }
}
if ($FixtureName) {
    $fixtures = @($fixtures | Where-Object { $_.name -eq $FixtureName })
    if ($fixtures.Count -ne 1) { throw "Expected exactly one fixture named $FixtureName." }
}
$results = @()
foreach ($fixture in $fixtures) {
    if ($fixture.status -ne 'passed') { $results += @{ fixture=$fixture.name; status='pending'; reason='Fixture oracle unavailable' }; continue }
    if ($fixture.name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Unsafe fixture name.' }
    $report = Join-Path $output "$($fixture.name).json"
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'Vidsc.App.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $app
    foreach ($argument in @('--presentation-check','--media',$fixture.file,'--fixture-manifest',$manifestPath,
        '--winapp-path',$winapp,'--report',$report,'--log',(Join-Path $output "$($fixture.name).log"))) {
        $start.ArgumentList.Add([string]$argument)
    }
    if ($ExhaustiveSteps) { $start.ArgumentList.Add('--exhaustive-steps') }
    if ($BoundarySeeks) { $start.ArgumentList.Add('--boundary-seeks') }
    if ($Lifecycle) {
        foreach ($argument in @('--presentation-lifecycle', '--media', $second[0].file)) { $start.ArgumentList.Add([string]$argument) }
    }
    $start.Environment['VIDSC_NATIVE_DIR'] = Join-Path $app 'native/flyleaf'
    $start.Environment['VIDSC_FFMPEG_DIR'] = Join-Path $app 'tools/ffmpeg'
    $start.Environment['VIDSC_SCENE_DIR'] = Join-Path $app 'scene'
    $start.Environment['VIDSC_WORK_DIR'] = Join-Path $output "$($fixture.name)-work"
    $process = [Diagnostics.Process]::Start($start)
    try {
        # The parent automation process owns foreground activation; Windows can deny a
        # background application's attempt to activate itself or its own child helper.
        $ready = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and $ready.Elapsed.TotalSeconds -lt 15) {
            Start-Sleep -Milliseconds 50
            $process.Refresh()
        }
        $focus = & $winapp ui focus NavigationTimeline -a $process.Id --json 2>&1
        $focus | Set-Content -LiteralPath (Join-Path $output "$($fixture.name)-focus.json")
        if ($LASTEXITCODE -ne 0 -and "$focus" -notlike '*foreground_not_target*') { throw "Owned acceptance window could not acquire foreground focus: $focus" }
        $exited = $process.WaitForExit($(if ($ExhaustiveSteps) { 1200000 } else { 300000 }))
        if (!$exited) {
            $process.Kill($true)
            if (!$process.WaitForExit(5000)) { throw "Owned app PID $($process.Id) remains alive after timeout cleanup; stop replacement runs." }
        }
        $evidence = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } else { $null }
        $results += @{ fixture=$fixture.name; status=$(if (!$exited) {'timeout'} elseif ($process.ExitCode -eq 0) {'passed-visible-identity-and-pts'} else {'failed'}); report=$report; exitCode=$(if ($exited) {$process.ExitCode} else {$null}); presentationEndStatus=$evidence.presentationEndStatus; sourceSwitchStatus=$evidence.sourceSwitchStatus; lifecycleRequested=[bool]$Lifecycle }
    }
    finally {
        try {
            if (!$process.HasExited) {
                $process.Kill($true)
                if (!$process.WaitForExit(5000)) { throw "Owned app PID $($process.Id) remains alive after cleanup; stop replacement runs." }
            }
        }
        finally { $process.Dispose() }
    }
    if ($results[-1].status -ne 'passed-visible-identity-and-pts') { break }
}
$summary = @{ schemaVersion=1; fullM1Passed=$false; presentationEndProvenance='See per-fixture reports; complete file map and independent mathematical oracle'; sourceSwitchStatus=$(if (!$Lifecycle) {'pending'} elseif (@($results | Where-Object { $_.sourceSwitchStatus -ne 'passed' }).Count) {'failed'} else {'passed'}); lifecycleRequested=[bool]$Lifecycle; results=$results }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Presentation identity evidence: $output (full M1 remains pending)."
if (@($results | Where-Object { $_.status -ne 'passed-visible-identity-and-pts' }).Count -gt 0) { exit 1 }
