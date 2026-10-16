#Requires -Version 7.0
<#
.SYNOPSIS
    Builds, verifies and promotes the vidsc Release bundle.

.DESCRIPTION
    Publishes the WPF application framework-dependent for win-x64, assembles the runtime bundle
    (pinned Flyleaf native libraries, the separate FFmpeg CLI tool set, the local scene runtime),
    verifies the assembled bundle from the staging location, and only then promotes it to
    artifacts/release. The previous release is retained when anything fails.

    The delivery layout is a bundle, not a single EXE: the pinned playback native set and the CLI
    tools must remain separate files, and the Python scene runtime is delivered as a directory tree.
    That is an explicit project decision recorded in docs/validation/2026-10-14-m0-m1.md.

.PARAMETER Run
    Launch the freshly published GUI after a successful publish and verification.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER RuntimeIdentifier
    Target runtime identifier. Defaults to win-x64.

.PARAMETER SkipVerify
    Skip bundle verification. This weakens the delivery gate and is reported explicitly.

.PARAMETER SkipSceneBundle
    Do not copy the local scene runtime into the bundle; the scene sub-gate is then reported as not
    verified for this build.

.PARAMETER SkipSmokeTest
    Do not run the scripted application smoke test.

.PARAMETER SkipFixtureGeneration
    Reuse an existing smoke fixture instead of regenerating it with the packaged CLI.

.EXAMPLE
    pwsh -NoProfile -File tools/Build-Release.ps1
    pwsh -NoProfile -File tools/Build-Release.ps1 -Run
#>
[CmdletBinding()]
param(
    [switch] $Run,
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [switch] $SkipVerify,
    [switch] $SkipSceneBundle,
    [switch] $SkipSmokeTest,
    [switch] $SkipFixtureGeneration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Native commands that write to stderr must not be promoted to terminating errors; exit codes are
# checked explicitly instead.
$PSNativeCommandUseErrorActionPreference = $false

# ---------------------------------------------------------------- paths ------

$ProjectRoot   = Split-Path -Parent $PSScriptRoot
$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts'))
$StagingRoot   = Join-Path $ArtifactsRoot 'staging'
$BuildDir      = Join-Path $StagingRoot 'build'
$StageRelease  = Join-Path $StagingRoot 'release'
$ReleaseRoot   = Join-Path $ArtifactsRoot 'release'
$TrashRoot     = Join-Path $ArtifactsRoot 'trash'
$DownloadsRoot = Join-Path $ArtifactsRoot 'downloads'
$TestsRoot     = Join-Path $ArtifactsRoot 'tests'
$FixtureRoot   = Join-Path $TestsRoot 'fixtures'
$VerifyRoot    = Join-Path $TestsRoot 'release-verify'
$AppProject    = Join-Path $ProjectRoot 'src/Vidsc.App/Vidsc.App.csproj'
$PinnedManifestPath = Join-Path $PSScriptRoot 'runtime-manifest.json'
$PromotionPath = Join-Path $PSScriptRoot 'Promotion.ps1'

. $PromotionPath

$script:StepResults = [System.Collections.Generic.List[object]]::new()

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "=== $Message" -ForegroundColor Cyan
}

function Assert-UnderArtifacts {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Purpose)

    $resolved = [System.IO.Path]::GetFullPath($Path)

    if (-not $resolved.StartsWith($ArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to $Purpose '$resolved': it is not inside the owned artifacts directory '$ArtifactsRoot'."
    }

    return $resolved
}

function Remove-Owned {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }

    $resolved = Assert-UnderArtifacts -Path $Path -Purpose 'remove'
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $Description,
        [string] $WorkingDirectory = $ProjectRoot
    )

    Write-Host "-> $Description"
    Write-Host ("   {0} {1}" -f $Executable, ($Arguments -join ' ')) -ForegroundColor DarkGray

    # Run from the requested directory so that, for example, `dotnet` resolves the project's
    # global.json regardless of the caller's current location.
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $Executable @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        $tail = ($output | Select-Object -Last 40) -join [Environment]::NewLine
        throw "$Description failed with exit code $exitCode.$([Environment]::NewLine)$tail"
    }

    return $output
}

function Invoke-Capture {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    $output = & $Executable @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Invoke-ProcessWithTimeout {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][int] $TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)

    if (-not $exited) {
        try { $process.Kill($true) } catch { }
        try { $null = $process.WaitForExit(10000) } catch { }

        return [pscustomobject]@{
            ExitCode  = -999
            TimedOut  = $true
            Output    = "the application did not exit within $TimeoutSeconds s and was terminated"
        }
    }

    return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $false; Output = '' }
}

function Add-StepResult {
    param([string] $Step, [string] $Status, [string] $Detail)
    $script:StepResults.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'pass' { 'Green' } 'fail' { 'Red' } 'pending' { 'Yellow' } default { 'Gray' } }
    Write-Host ("[{0,-7}] {1}: {2}" -f $Status, $Step, $Detail) -ForegroundColor $colour
}

function Get-ReleaseExecutable {
    return Join-Path $ReleaseRoot 'app/Vidsc.App.exe'
}

<#
.SYNOPSIS
    Generates a tiny labeled synthetic fixture with three colour-coded seconds.
.DESCRIPTION
    Second 0 is red, second 1 is green and second 2 is blue, so a decoded frame's identity can be
    checked numerically (no OCR, no hard-coded source media path). The audio track is a 440 Hz sine.
#>
function Invoke-Fixture {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $FrameRate
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null

    $ffmpeg = Join-Path $StageRelease 'app/tools/ffmpeg/ffmpeg.exe'

    $fixtureArgs = @(
        '-hide_banner', '-nostdin', '-v', 'error', '-y',
        '-f', 'lavfi', '-i', ('color=c=red:s=320x180:r={0}:d=1' -f $FrameRate),
        '-f', 'lavfi', '-i', ('color=c=green:s=320x180:r={0}:d=1' -f $FrameRate),
        '-f', 'lavfi', '-i', ('color=c=blue:s=320x180:r={0}:d=1' -f $FrameRate),
        '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=3',
        '-filter_complex', '[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]',
        '-map', '[v]', '-map', '3:a',
        '-c:v', 'libx264', '-profile:v', 'high', '-pix_fmt', 'yuv420p', '-g', '24', '-bf', '2',
        '-c:a', 'aac', '-b:a', '64k', '-shortest',
        $Path
    )

    return Invoke-Native -Executable $ffmpeg -Arguments $fixtureArgs -Description "generate the labeled fixture at $FrameRate fps"
}

<#
.SYNOPSIS
    Generates the deterministic scene-detection fixture: three colour-coded two-second segments.
.DESCRIPTION
    Cuts at 2 s and 4 s are detectable at the packaged worker's default thresholds, so it is a stable
    scene sub-gate input. It is generated with the packaged ffmpeg and is never committed.
#>
function Invoke-SceneFixture {
    param([Parameter(Mandatory)][string] $Path)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null

    $ffmpeg = Join-Path $StageRelease 'app/tools/ffmpeg/ffmpeg.exe'

    $sceneArgs = @(
        '-hide_banner', '-nostdin', '-v', 'error', '-y',
        '-f', 'lavfi', '-i', 'color=c=red:s=320x180:r=24:d=2',
        '-f', 'lavfi', '-i', 'color=c=green:s=320x180:r=24:d=2',
        '-f', 'lavfi', '-i', 'color=c=blue:s=320x180:r=24:d=2',
        '-filter_complex', '[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]',
        '-map', '[v]',
        '-c:v', 'libx264', '-profile:v', 'high', '-pix_fmt', 'yuv420p', '-g', '24', '-bf', '2',
        $Path
    )

    return Invoke-Native -Executable $ffmpeg -Arguments $sceneArgs -Description 'generate the scene-detection fixture'
}

# ------------------------------------------------------------- preflight -----

Write-Step "Preflight"

if (-not (Test-Path -LiteralPath $PinnedManifestPath)) {
    throw "The pinned runtime manifest is missing: $PinnedManifestPath"
}

$pinned = Get-Content -LiteralPath $PinnedManifestPath -Raw | ConvertFrom-Json

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { throw 'The dotnet CLI was not found on PATH.' }

# Resolve the SDK from the project root so global.json (10.0.201) is honoured no matter where the
# caller invoked this script from.
Push-Location -LiteralPath $ProjectRoot
try {
    $sdkVersion = (& dotnet --version) | Select-Object -First 1
    $runtimeList = & dotnet --list-runtimes
}
finally {
    Pop-Location
}

Write-Host "dotnet SDK       : $sdkVersion"

$desktopRuntime = ($runtimeList | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' } | Select-Object -First 1)
if (-not $desktopRuntime) {
    throw 'The .NET 10 Desktop Runtime (Microsoft.WindowsDesktop.App 10.x) is required but was not found.'
}
Write-Host "desktop runtime  : $desktopRuntime"

# Never stop the user's running application: refuse and keep the previous release.
$running = Get-Process -Name 'Vidsc.App' -ErrorAction SilentlyContinue
if ($running) {
    $runningPath = $null
    try { $runningPath = ($running | Select-Object -First 1).Path } catch { $runningPath = $null }

    if ($runningPath -and $runningPath.StartsWith($ReleaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Vidsc.App is currently running from '$ReleaseRoot'. Close it and run the build again; the previous release is kept intact."
    }
}

if (-not $SkipVerify) {
    $sevenZip = Join-Path ${env:ProgramFiles} '7-Zip/7z.exe'
    if (-not (Test-Path -LiteralPath $sevenZip)) {
        throw "7-Zip is required to extract the pinned Flyleaf native archive and was not found at '$sevenZip'."
    }
}
else {
    $sevenZip = Join-Path ${env:ProgramFiles} '7-Zip/7z.exe'
}

# ------------------------------------------------- pinned native bundle -----

Write-Step "Pinned playback native set"

$flyleafVersion = $pinned.playback.package.version
$nativeSrcRoot  = Join-Path $DownloadsRoot "flyleaf-$flyleafVersion"
$expectedArchive = Join-Path $DownloadsRoot ("Flyleaf_v{0}.7z" -f $flyleafVersion)

$nativeReady = (Test-Path -LiteralPath $nativeSrcRoot) -and
    (@($pinned.playback.native.libraries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $nativeSrcRoot $_)) }).Count -eq 0)

if (-not $nativeReady) {
    New-Item -ItemType Directory -Force -Path $DownloadsRoot | Out-Null

    if (-not (Test-Path -LiteralPath $expectedArchive)) {
        Write-Host "Downloading $($pinned.playback.native.releaseUrl)"
        Invoke-WebRequest -Uri $pinned.playback.native.releaseUrl -OutFile $expectedArchive
    }

    $archiveHash = Get-Sha256 -Path $expectedArchive
    if ($archiveHash -ne $pinned.playback.native.sha256.ToLowerInvariant()) {
        throw "The Flyleaf archive hash does not match the pinned value. Expected $($pinned.playback.native.sha256), got $archiveHash."
    }

    Remove-Owned -Path $nativeSrcRoot
    New-Item -ItemType Directory -Force -Path $nativeSrcRoot | Out-Null

    $null = Invoke-Native -Executable $sevenZip `
        -Arguments @('x', $expectedArchive, "-o$nativeSrcRoot", 'FFmpeg/*', '-y', '-bso0', '-bsp0') `
        -Description 'extract the pinned Flyleaf native libraries'

    # The archive layout is FFmpeg/<library>; flatten it to the native root.
    $extracted = Join-Path $nativeSrcRoot 'FFmpeg'
    if (Test-Path -LiteralPath $extracted) {
        Get-ChildItem -LiteralPath $extracted -File | ForEach-Object {
            Move-Item -LiteralPath $_.FullName -Destination (Join-Path $nativeSrcRoot $_.Name) -Force
        }
        Remove-Owned -Path $extracted
    }

    Add-StepResult -Step 'flyleaf-native-download' -Status 'pass' -Detail "sha256 $archiveHash"
}
else {
    Add-StepResult -Step 'flyleaf-native-download' -Status 'pass' -Detail "cached in $nativeSrcRoot"
}

$missingNative = @($pinned.playback.native.libraries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $nativeSrcRoot $_)) })
if ($missingNative.Count -gt 0) {
    throw "Extracted Flyleaf native set is incomplete. Missing: $($missingNative -join ', ')"
}

# --------------------------------------------------------- CLI tool set -----

Write-Step "CLI FFmpeg tool set"

$cliSource = $pinned.cli.sourceDirectory
if (-not (Test-Path -LiteralPath $cliSource)) {
    throw "The configured FFmpeg CLI directory does not exist: $cliSource"
}

$cliFiles = @($pinned.cli.executables) + @($pinned.cli.sharedLibraries)
$missingCli = @($cliFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $cliSource $_)) })
if ($missingCli.Count -gt 0) {
    throw "The FFmpeg CLI source is incomplete. Missing: $($missingCli -join ', ')"
}

$ffmpegProbe = Invoke-Capture -Executable (Join-Path $cliSource 'ffmpeg.exe') -Arguments @('-hide_banner', '-version')
if ($ffmpegProbe.ExitCode -ne 0) { throw "ffmpeg -version failed with exit code $($ffmpegProbe.ExitCode)." }

if ($ffmpegProbe.Output -notmatch [regex]::Escape($pinned.cli.expectedVersionToken)) {
    throw "The FFmpeg build does not match the pinned version token '$($pinned.cli.expectedVersionToken)'. First line: $(($ffmpegProbe.Output -split "`n")[0])"
}

Add-StepResult -Step 'cli-identity' -Status 'pass' -Detail "token $($pinned.cli.expectedVersionToken) found"

# ------------------------------------------------------------- staging ------

Write-Step "Staging"

Remove-Owned -Path $BuildDir
Remove-Owned -Path $StageRelease
New-Item -ItemType Directory -Force -Path $BuildDir, $StageRelease | Out-Null

$appStage = Join-Path $StageRelease 'app'
New-Item -ItemType Directory -Force -Path $appStage | Out-Null

$publishArgs = @(
    'publish', $AppProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'false',
    '-o', $BuildDir,
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-p:UseAppHost=true',
    '--nologo'
)

$null = Invoke-Native -Executable 'dotnet' -Arguments $publishArgs -Description "publish $Configuration $RuntimeIdentifier (framework-dependent)"

$appExe = Join-Path $appStage 'Vidsc.App.exe'
Get-ChildItem -LiteralPath $BuildDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $appStage -Recurse -Force
}

if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Publish did not produce $appExe"
}

# The .NET runtime must not be bundled.
if (Test-Path -LiteralPath (Join-Path $appStage 'System.Private.CoreLib.dll')) {
    throw 'The publish output contains .NET runtime files; the delivery contract is framework-dependent.'
}

New-Item -ItemType Directory -Force -Path (Join-Path $appStage 'native/flyleaf') | Out-Null
foreach ($library in $pinned.playback.native.libraries) {
    Copy-Item -LiteralPath (Join-Path $nativeSrcRoot $library) -Destination (Join-Path $appStage 'native/flyleaf') -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $appStage 'tools/ffmpeg') | Out-Null
foreach ($file in $cliFiles) {
    Copy-Item -LiteralPath (Join-Path $cliSource $file) -Destination (Join-Path $appStage 'tools/ffmpeg') -Force
}

Copy-Item -LiteralPath $PinnedManifestPath -Destination (Join-Path $appStage 'runtime-manifest.json') -Force

$sceneStage = Join-Path $appStage 'scene'
New-Item -ItemType Directory -Force -Path $sceneStage | Out-Null

$sceneDriverSource = Join-Path $PSScriptRoot 'scene/vidsc_scene_job.py'
Copy-Item -LiteralPath $sceneDriverSource -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_inspection_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_waveform_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_thumbnail_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_copy_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_av1_codec.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_nal_streaming.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_audio_streaming.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_matroska_audio.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_audio_copy_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_audio_copy.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_accurate_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_accurate_codec.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_accurate_audio.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_matroska_copy_job.py') -Destination $sceneStage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scene/vidsc_copy_discovery_job.py') -Destination $sceneStage -Force

# The scene runtime is preferred in its RELOCATABLE form (an official embeddable Python plus
# project-local wheels, produced by tools/Build-Scene-Runtime.ps1). A legacy virtual environment is
# only a local-development fallback: it references the host Python installation and is reported as
# not relocatable, so it can never be passed off as a self-contained bundle.
$relocatableRuntime = Join-Path $ArtifactsRoot 'runtime/scene/python'
$relocatableManifest = Join-Path $ArtifactsRoot 'runtime/scene/scene-runtime-manifest-relocatable.json'
$legacyVenv = Join-Path $ArtifactsRoot 'runtime/scene/venv'
$legacyManifest = Join-Path $ArtifactsRoot 'runtime/scene/scene-runtime-manifest.json'

$sceneStatus = 'missing'
$sceneDetail = 'No scene runtime was bundled for this build.'
$sceneRelativePython = $null
$sceneRelocatable = $false

if (-not $SkipSceneBundle) {
    if ((Test-Path -LiteralPath (Join-Path $relocatableRuntime 'python.exe')) -and (Test-Path -LiteralPath $relocatableManifest)) {
        $sceneTarget = Join-Path $sceneStage 'python'
        Assert-UnderArtifacts -Path $sceneTarget -Purpose 'write' | Out-Null

        $robocopy = Invoke-Capture -Executable 'robocopy' -Arguments @(
            $relocatableRuntime, $sceneTarget, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1'
        )

        if ($robocopy.ExitCode -ge 8) {
            throw "Copying the relocatable scene runtime failed (robocopy exit $($robocopy.ExitCode))."
        }

        Copy-Item -LiteralPath $relocatableManifest -Destination $sceneStage -Force
        $sceneStatus = 'relocatable'
        $sceneRelocatable = $true
        $sceneRelativePython = 'scene/python/python.exe'
        $sceneDetail = "relocatable embeddable runtime copied to scene/python ($((Get-ChildItem -LiteralPath $sceneTarget -Recurse -File | Measure-Object).Count) files)"
        Add-StepResult -Step 'scene-bundle' -Status 'pass' -Detail $sceneDetail
    }
    elseif ((Test-Path -LiteralPath (Join-Path $legacyVenv 'Scripts/python.exe')) -and (Test-Path -LiteralPath $legacyManifest)) {
        $sceneTarget = Join-Path $sceneStage 'venv'
        Assert-UnderArtifacts -Path $sceneTarget -Purpose 'write' | Out-Null

        $robocopy = Invoke-Capture -Executable 'robocopy' -Arguments @(
            $legacyVenv, $sceneTarget, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1'
        )

        if ($robocopy.ExitCode -ge 8) {
            throw "Copying the scene runtime failed (robocopy exit $($robocopy.ExitCode))."
        }

        Copy-Item -LiteralPath $legacyManifest -Destination $sceneStage -Force
        $sceneStatus = 'legacy'
        $sceneRelocatable = $false
        $sceneRelativePython = 'scene/venv/Scripts/python.exe'
        $sceneDetail = "LEGACY host-Python venv copied to scene/venv (NOT relocatable); build the embeddable runtime with tools/Build-Scene-Runtime.ps1 to close the scene/runtime checkpoint"
        Add-StepResult -Step 'scene-bundle' -Status 'pending' -Detail $sceneDetail
    }
    else {
        $sceneDetail = 'No scene runtime was found under artifacts/runtime/scene (expected a relocatable python/ runtime or a legacy venv/), so the scene sub-gate is NOT verified.'
        Add-StepResult -Step 'scene-bundle' -Status 'fail' -Detail $sceneDetail
    }
}
else {
    Add-StepResult -Step 'scene-bundle' -Status 'pending' -Detail 'skipped by -SkipSceneBundle'
}

$sceneBundled = $sceneStatus -in @('relocatable', 'legacy')
$scenePythonStage = if ($sceneRelativePython) { Join-Path $appStage $sceneRelativePython } else { $null }

# --------------------------------------------- observed runtime manifest ----

Write-Step "Observed runtime manifest"

$observedPath = Join-Path $appStage 'runtime-manifest.observed.json'

function New-ObservedManifest {
    param([string] $Root)

    $files = [System.Collections.Generic.List[object]]::new()
    $sceneVenv = Join-Path $Root 'scene/venv'
    $scenePython = Join-Path $Root 'scene/python'

    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')

        if ($relative.StartsWith('scene/venv/') -or $relative.StartsWith('scene/python/')) { return }

        $files.Add([ordered]@{
                path      = $relative
                sizeBytes = $_.Length
                sha256    = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            })
    }

    $sceneVenvFiles = 0
    $sceneVenvBytes = 0
    $sceneRuntimeRoot = if (Test-Path -LiteralPath $scenePython) { $scenePython } else { $sceneVenv }
    if (Test-Path -LiteralPath $sceneRuntimeRoot) {
        $sceneItems = Get-ChildItem -LiteralPath $sceneRuntimeRoot -Recurse -File
        $sceneVenvFiles = $sceneItems.Count
        $sceneVenvBytes = ($sceneItems | Measure-Object -Property Length -Sum).Sum
    }

    $ffmpegVersion = ((Invoke-Capture -Executable (Join-Path $Root 'tools/ffmpeg/ffmpeg.exe') -Arguments @('-hide_banner', '-version')).Output -split "`n")[0]
    $ffprobeVersion = ((Invoke-Capture -Executable (Join-Path $Root 'tools/ffmpeg/ffprobe.exe') -Arguments @('-hide_banner', '-version')).Output -split "`n")[0]

    $managedPlayback = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Root 'FlyleafLib.dll'))
    if ($managedPlayback.ProductVersion -ne $pinned.playback.sourceBuild.informationalVersion) {
        throw "The playback assembly identity does not match the pinned local source build: $($managedPlayback.ProductVersion)"
    }
    $vendorRoot = Join-Path $ProjectRoot $pinned.playback.sourceBuild.directory
    $sourceFiles = @(Get-ChildItem -LiteralPath $vendorRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.xaml', '.csproj', '.props', '.hlsl')
    } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($vendorRoot.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })

    $document = [ordered]@{
        schemaVersion      = 1
        generatedUtc       = (Get-Date).ToUniversalTime().ToString('O')
        releaseDirectory   = $Root
        configuration      = $Configuration
        runtimeIdentifier  = $RuntimeIdentifier
        sdkVersion         = $sdkVersion
        desktopRuntime     = $desktopRuntime
        flyleafVersion     = $pinned.playback.package.version
        flyleafManagedBuild = [ordered]@{
            informationalVersion = $managedPlayback.ProductVersion
            upstreamCommit = $pinned.playback.sourceBuild.upstreamCommit
            sourceFiles = $sourceFiles
        }
        flyleafNativeSha   = [ordered]@{
            archive = $pinned.playback.native.sha256
            source  = $pinned.playback.native.releaseUrl
        }
        ffmpegVersion      = $ffmpegVersion
        ffprobeVersion     = $ffprobeVersion
        sceneBundled       = $sceneBundled
        sceneRelocatable   = $sceneRelocatable
        sceneRuntimePath   = $sceneRelativePython
        sceneVenvFileCount = $sceneVenvFiles
        sceneVenvBytes     = $sceneVenvBytes
        fileCount          = $files.Count
        files              = $files
    }

    $json = $document | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath $observedPath -Value $json -Encoding utf8
}

New-ObservedManifest -Root $appStage
Add-StepResult -Step 'observed-manifest' -Status 'pass' -Detail $observedPath

# ---------------------------------------------------------- verification ----

$verifyFailures = [System.Collections.Generic.List[string]]::new()

function Add-VerifyFailure {
    param([string] $Message)
    $verifyFailures.Add($Message)
    Write-Host "   ! $Message" -ForegroundColor Red
}

if ($SkipVerify) {
    Add-StepResult -Step 'bundle-verification' -Status 'pending' -Detail 'skipped by -SkipVerify (delivery gate weakened)'
}
else {
    Write-Step "Bundle verification (from the staging location)"

    # 1. Required native libraries and CLI tools are present.
    foreach ($library in $pinned.playback.native.libraries) {
        if (-not (Test-Path -LiteralPath (Join-Path $appStage "native/flyleaf/$library"))) {
            Add-VerifyFailure "Missing Flyleaf native library: $library"
        }
    }

    foreach ($file in $cliFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $appStage "tools/ffmpeg/$file"))) {
            Add-VerifyFailure "Missing CLI file: $file"
        }
    }

    # 2. The CLI is exercised with the development FFmpeg directory removed from PATH.
    $savedPath = $env:PATH
    $pathWithoutDevFfmpeg = ($savedPath -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $cliSource.TrimEnd('\')) }) -join ';'

    Remove-Owned -Path $VerifyRoot
    New-Item -ItemType Directory -Force -Path $VerifyRoot | Out-Null

    try {
        $env:PATH = $pathWithoutDevFfmpeg

        $stagedFfmpeg = Join-Path $appStage 'tools/ffmpeg/ffmpeg.exe'
        $stagedFfprobe = Join-Path $appStage 'tools/ffmpeg/ffprobe.exe'

        if (-not $SkipFixtureGeneration) {
            $fixture = Join-Path $FixtureRoot 'm0m1-labeled-24fps.mp4'
            Invoke-Fixture -Path $fixture -FrameRate '24' | Out-Null
            $fixtureFractional = Join-Path $FixtureRoot 'm0m1-labeled-24000-1001.mp4'
            Invoke-Fixture -Path $fixtureFractional -FrameRate '24000/1001' | Out-Null

            $sceneFixtureForGeneration = Join-Path $FixtureRoot 'scene-smoke.mp4'
            if (-not (Test-Path -LiteralPath $sceneFixtureForGeneration)) {
                Invoke-SceneFixture -Path $sceneFixtureForGeneration | Out-Null
            }
        }

        $fixture = Join-Path $FixtureRoot 'm0m1-labeled-24fps.mp4'
        if (-not (Test-Path -LiteralPath $fixture)) {
            Add-VerifyFailure "The labeled smoke fixture is missing: $fixture"
        }
        else {
            $fixtureProbe = Invoke-Capture -Executable $stagedFfprobe -Arguments @(
                '-hide_banner', '-v', 'error', '-print_format', 'json', '-show_streams', $fixture
            )

            if ($fixtureProbe.ExitCode -ne 0) {
                Add-VerifyFailure "The packaged ffprobe failed to probe the fixture (exit $($fixtureProbe.ExitCode))."
            }
            elseif ($fixtureProbe.Output -notmatch '"codec_name": "h264"') {
                Add-VerifyFailure 'The packaged ffprobe did not report h264 for the labeled fixture.'
            }
            else {
                Add-StepResult -Step 'packaged-cli-probe' -Status 'pass' -Detail 'ffprobe probed the labeled fixture without the development FFmpeg on PATH'
            }

            $copyOutput = Join-Path $VerifyRoot 'packaged-copy-export.mp4'
            $export = Invoke-Capture -Executable $stagedFfmpeg -Arguments @(
                '-hide_banner', '-nostdin', '-v', 'error', '-y',
                '-ss', '0.5', '-i', $fixture, '-t', '1',
                '-map', '0:v:0', '-c:v', 'copy', '-map', '0:a:0?', '-c:a', 'copy',
                '-avoid_negative_ts', 'disabled', $copyOutput
            )

            if ($export.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $copyOutput)) {
                Add-VerifyFailure "The packaged ffmpeg stream-copy export failed (exit $($export.ExitCode)). $($export.Output)"
            }
            else {
                Add-StepResult -Step 'packaged-cli-export' -Status 'pass' -Detail "stream copy produced $((Get-Item -LiteralPath $copyOutput).Length) bytes"
            }

            # Colour-coded oracle: second 0 is red, second 1 is green, second 2 is blue.
            $expectations = @(
                @{ Time = '0.5'; Name = 'red';   Test = { param($r, $g, $b) $r -gt 200 -and $g -lt 60 -and $b -lt 60 } },
                @{ Time = '1.5'; Name = 'green'; Test = { param($r, $g, $b) $g -gt 120 -and $r -lt 60 -and $b -lt 60 } },
                @{ Time = '2.5'; Name = 'blue';  Test = { param($r, $g, $b) $b -gt 200 -and $r -lt 60 -and $g -lt 60 } }
            )

            foreach ($expectation in $expectations) {
                $rawPath = Join-Path $VerifyRoot ("frame-{0}.raw" -f $expectation.Name)
                $null = Invoke-Native -Executable $stagedFfmpeg -Arguments @(
                    '-hide_banner', '-v', 'error', '-y', '-ss', $expectation.Time, '-i', $fixture,
                    '-frames:v', '1', '-vf', 'crop=4:4:160:90,format=rgb24', '-f', 'rawvideo', $rawPath
                ) -Description "decode the $($expectation.Name) segment from the packaged fixture"

                $bytes = [System.IO.File]::ReadAllBytes($rawPath)
                if ($bytes.Length -lt 3) {
                    Add-VerifyFailure "Frame $($expectation.Time)s produced no pixel data."
                    continue
                }

                $meanR = 0; $meanG = 0; $meanB = 0; $count = [int]($bytes.Length / 3)
                for ($i = 0; $i -lt $count; $i++) {
                    $meanR += $bytes[$i * 3]; $meanG += $bytes[$i * 3 + 1]; $meanB += $bytes[$i * 3 + 2]
                }
                $meanR = [int]($meanR / $count); $meanG = [int]($meanG / $count); $meanB = [int]($meanB / $count)

                if (& $expectation.Test $meanR $meanG $meanB) {
                    Add-StepResult -Step ("labeled-frame-" + $expectation.Name) -Status 'pass' -Detail "t=$($expectation.Time)s rgb=($meanR,$meanG,$meanB)"
                }
                else {
                    Add-VerifyFailure "t=$($expectation.Time)s was expected to be $($expectation.Name) but decoded rgb=($meanR,$meanG,$meanB)."
                }
            }
        }

        # 3. The packaged scene job runs with the development FFmpeg directory off PATH.
        if ($sceneBundled) {
            $scenePython = $scenePythonStage
            $sceneDriver = Join-Path $appStage 'scene/vidsc_scene_job.py'
            $sceneSource = Join-Path $FixtureRoot 'scene-smoke.mp4'

            if (-not (Test-Path -LiteralPath $sceneSource)) {
                Add-VerifyFailure "The scene smoke fixture is missing: $sceneSource"
            }
            else {
                $sceneReport = Join-Path $VerifyRoot 'packaged-scene-job.json'
                $sceneRun = Invoke-Capture -Executable $scenePython -Arguments @(
                    $sceneDriver, '--input', $sceneSource, '--output', $sceneReport, '--detector', 'content', '--threshold', '12'
                )

                if ($sceneRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $sceneReport)) {
                    Add-VerifyFailure "The packaged scene job failed (exit $($sceneRun.ExitCode)). $($sceneRun.Output)"
                }
                else {
                    $sceneJson = Get-Content -LiteralPath $sceneReport -Raw | ConvertFrom-Json
                    $cutCount = @($sceneJson.scenes).Count
                    $cutTimes = (@($sceneJson.scenes) | ForEach-Object { '{0:0.000}' -f $_.startSeconds }) -join ', '
                    $fullFrames = [int]$sceneJson.framesProcessed

                    if (-not $sceneJson.success -or $cutCount -lt 2) {
                        Add-VerifyFailure "The packaged scene job returned success=$($sceneJson.success) with $cutCount scenes. $($sceneJson.error)"
                    }
                    else {
                        Add-StepResult -Step 'packaged-scene-job' -Status 'pass' -Detail "$cutCount scenes at [$cutTimes] s, backend=$($sceneJson.backend), frames=$fullFrames"
                    }

                    # Defect 6: --start 0 --duration 2 must actually bound the analysis, not scan the
                    # whole source while only echoing the requested duration in the report.
                    $windowReport = Join-Path $VerifyRoot 'packaged-scene-window.json'
                    $windowRun = Invoke-Capture -Executable $scenePython -Arguments @(
                        $sceneDriver, '--input', $sceneSource, '--output', $windowReport,
                        '--detector', 'content', '--threshold', '12', '--start', '0', '--duration', '2'
                    )

                    if ($windowRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $windowReport)) {
                        Add-VerifyFailure "The windowed packaged scene job failed (exit $($windowRun.ExitCode)). $($windowRun.Output)"
                    }
                    else {
                        $windowJson = Get-Content -LiteralPath $windowReport -Raw | ConvertFrom-Json
                        $windowFrames = [int]$windowJson.framesProcessed
                        $windowEnd = $windowJson.analyzedEndSeconds

                        if ($windowEnd -ne 2) {
                            Add-VerifyFailure "The windowed scene job reported analyzedEndSeconds=$windowEnd, expected 2."
                        }
                        elseif ($windowFrames -le 0 -or ($fullFrames -gt 0 -and $windowFrames -ge $fullFrames)) {
                            Add-VerifyFailure "The windowed scene job processed $windowFrames frames versus $fullFrames for the whole source; the duration was not honoured."
                        }
                        else {
                            Add-StepResult -Step 'packaged-scene-window' -Status 'pass' -Detail "duration honoured: end=$windowEnd s, frames=$windowFrames (full=$fullFrames)"
                        }
                    }

                    # Adaptive detector must receive the requested threshold as its ACTUAL parameter.
                    # Cover both zero/nonzero origins and windows with/without a cut. A single
                    # zero-origin smoke cannot detect an unsupported start_time API or a fabricated
                    # processed count based on the absolute ending position.
                    foreach ($case in @(
                        @{ Name = 'zero-no-cut'; Start = '0'; Duration = '0.75'; Frames = 18; Last = 17; Scenes = 1 },
                        @{ Name = 'zero-cut'; Start = '0'; Duration = '3'; Frames = 72; Last = 71; Scenes = 2 },
                        @{ Name = 'nonzero-no-cut'; Start = '2.25'; Duration = '0.5'; Frames = 12; Last = 65; Scenes = 1 },
                        @{ Name = 'nonzero-cut'; Start = '1'; Duration = '3'; Frames = 72; Last = 95; Scenes = 2 }
                    )) {
                        $caseReport = Join-Path $VerifyRoot ("packaged-scene-{0}.json" -f $case.Name)
                        $caseRun = Invoke-Capture -Executable $scenePython -Arguments @(
                            $sceneDriver, '--input', $sceneSource, '--output', $caseReport,
                            '--start', $case.Start, '--duration', $case.Duration, '--threshold', '12'
                        )
                        if ($caseRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $caseReport)) {
                            Add-VerifyFailure "Scene window $($case.Name) failed (exit $($caseRun.ExitCode))."
                            continue
                        }
                        $caseJson = Get-Content -LiteralPath $caseReport -Raw | ConvertFrom-Json
                        $caseScenes = @($caseJson.scenes).Count
                        # This generated fixture has an authored 24-fps schedule; production scene times use decoded PTS.
                        $lastSeconds = $case.Last / 24.0
                        if (-not $caseJson.success -or $caseJson.schemaVersion -ne 2 -or $caseJson.framesProcessed -ne $case.Frames -or
                            [Math]::Abs($caseJson.lastProcessedSeconds - $lastSeconds) -gt 0.000001 -or $caseScenes -ne $case.Scenes) {
                            Add-VerifyFailure "Scene window $($case.Name): frames=$($caseJson.framesProcessed), lastSeconds=$($caseJson.lastProcessedSeconds), scenes=$caseScenes."
                        }
                        else {
                            Add-StepResult -Step "scene-window-$($case.Name)" -Status 'pass' -Detail "frames=$($caseJson.framesProcessed); lastSeconds=$($caseJson.lastProcessedSeconds); scenes=$caseScenes"
                        }
                    }

                    $adaptiveReport = Join-Path $VerifyRoot 'packaged-scene-adaptive.json'
                    $adaptiveRun = Invoke-Capture -Executable $scenePython -Arguments @(
                        $sceneDriver, '--input', $sceneSource, '--output', $adaptiveReport,
                        '--detector', 'adaptive', '--threshold', '5'
                    )

                    if ($adaptiveRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $adaptiveReport)) {
                        Add-VerifyFailure "The adaptive packaged scene job failed (exit $($adaptiveRun.ExitCode)). $($adaptiveRun.Output)"
                    }
                    else {
                        $adaptiveJson = Get-Content -LiteralPath $adaptiveReport -Raw | ConvertFrom-Json
                        $config = $adaptiveJson.detectorConfig

                        if ($null -eq $config -or $config.parameter -ne 'adaptive_threshold' -or $config.adaptiveThreshold -ne 5) {
                            Add-VerifyFailure "The adaptive detector did not receive the requested threshold as adaptive_threshold (config: $($config | ConvertTo-Json -Compress))."
                        }
                        else {
                            Add-StepResult -Step 'packaged-scene-adaptive-threshold' -Status 'pass' -Detail "adaptive_threshold=$($config.adaptiveThreshold) wired to the detector"
                        }
                    }
                }
            }
        }
        elseif ($SkipSceneBundle) {
            Add-StepResult -Step 'packaged-scene-job' -Status 'pending' -Detail 'skipped by -SkipSceneBundle; scene sub-gate NOT verified'
        }
        else {
            Add-VerifyFailure 'No scene runtime was found to bundle (artifacts/runtime/scene); the scene sub-gate failed.'
        }

        # 4. The published application starts, plays, seeks and closes without leaving a stray process.
        if (-not $SkipSmokeTest) {
            $smokeReport = Join-Path $VerifyRoot 'app-smoke-report.json'
            $fixture = Join-Path $FixtureRoot 'm0m1-labeled-24fps.mp4'
            $fixtureFractional = Join-Path $FixtureRoot 'm0m1-labeled-24000-1001.mp4'

            $smokeArgs = @(
                '--smoke-test',
                '--report', $smokeReport,
                '--media', $fixture,
                '--media', $fixtureFractional
            )

            if ((Test-Path -LiteralPath $fixture) -and (Test-Path -LiteralPath $fixtureFractional)) {
                # Keep every file the verification run writes inside the owned verification directory.
                $env:VIDSC_WORK_DIR = Join-Path $VerifyRoot 'work'

                try {
                    $smoke = Invoke-ProcessWithTimeout -FilePath $appExe -Arguments $smokeArgs -TimeoutSeconds 180
                }
                finally {
                    Remove-Item Env:VIDSC_WORK_DIR -ErrorAction SilentlyContinue
                }

                if ($smoke.ExitCode -ne 0) {
                    $smokeFailures = ''
                    if (Test-Path -LiteralPath $smokeReport) {
                        try {
                            $smokeJson = Get-Content -LiteralPath $smokeReport -Raw | ConvertFrom-Json
                            $smokeFailures = (@($smokeJson.failures) -join '; ')
                        }
                        catch {
                            $smokeFailures = 'the smoke report could not be parsed'
                        }
                    }

                    Add-VerifyFailure "The application smoke test failed with exit code $($smoke.ExitCode). $($smoke.Output) Failures: $smokeFailures"
                }
                else {
                    $report = Get-Content -LiteralPath $smokeReport -Raw | ConvertFrom-Json
                    $adapterCount = @($report.engine.adapters).Count
                    $loadedNatives = @($report.engine.loadedNativeModules).Count
                    $surface = [bool]$report.engine.surfaceCreated
                    $passed = [bool]$report.passed

                    if (-not $surface -or -not $passed) {
                        Add-VerifyFailure "The smoke report is not clean: surfaceCreated=$surface passed=$passed failures=$(@($report.failures) -join '; ')"
                    }
                    else {
                        Add-StepResult -Step 'app-smoke-test' -Status 'pass' -Detail (
                            "exit 0; flyleaf ffmpeg $($report.engine.ffmpegVersion); adapters $adapterCount; natives $loadedNatives; hook $($report.hook.status)"
                        )
                    }
                }
            }
            else {
                Add-VerifyFailure 'The smoke fixtures are missing; the application smoke test could not run.'
            }
        }
        else {
            Add-StepResult -Step 'app-smoke-test' -Status 'pending' -Detail 'skipped by -SkipSmokeTest'
        }
    }
    finally {
        $env:PATH = $savedPath
    }

    if ($verifyFailures.Count -eq 0) {
        Add-StepResult -Step 'bundle-verification' -Status 'pass' -Detail 'all staged checks passed'
    }
    else {
        Add-StepResult -Step 'bundle-verification' -Status 'fail' -Detail "$($verifyFailures.Count) check(s) failed"
    }
}

# ------------------------------------------------------------ promotion -----

if ($verifyFailures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Verification failed. The previous release was NOT replaced.' -ForegroundColor Red
    foreach ($failure in $verifyFailures) { Write-Host "  - $failure" -ForegroundColor Red }
    Write-Host "Staging output retained at $StageRelease"
    exit 1
}

Write-Step "Promotion"

Assert-UnderArtifacts -Path $ReleaseRoot -Purpose 'replace' | Out-Null
Assert-UnderArtifacts -Path $StageRelease -Purpose 'move' | Out-Null
Assert-UnderArtifacts -Path $TrashRoot -Purpose 'write' | Out-Null

# The promotion transaction is extracted into tools/Promotion.ps1 so it can be exercised in
# isolation; it refuses a failed verification, moves the previous release aside, and restores it on a
# failed promotion move.
$promotion = Invoke-ReleasePromotion `
    -ReleaseRoot $ReleaseRoot `
    -StageRelease $StageRelease `
    -TrashRoot $TrashRoot `
    -VerifyFailures $verifyFailures

if (-not $promotion.Promoted) {
    if ($promotion.RollbackFailed) {
        Write-Host "Promotion failed AND the rollback failed: $($promotion.Error)" -ForegroundColor Red
        Write-Host "The previous release remains at $($promotion.BackupPath); restore it manually." -ForegroundColor Red
    }
    elseif ($promotion.RolledBack) {
        Write-Host "Promotion failed and the previous release was restored: $($promotion.Error)" -ForegroundColor Yellow
    }
    else {
        Write-Host "Promotion failed: $($promotion.Error)" -ForegroundColor Red
    }

    Add-StepResult -Step 'promotion' -Status 'fail' -Detail "reason=$($promotion.Reason); rollbackFailed=$($promotion.RollbackFailed)"
    exit 1
}

# Keep only the most recent previous release; prune older owned snapshots.
if (Test-Path -LiteralPath $TrashRoot) {
    $snapshots = Get-ChildItem -LiteralPath $TrashRoot -Directory | Sort-Object Name -Descending
    foreach ($snapshot in ($snapshots | Select-Object -Skip 1)) {
        Remove-Owned -Path $snapshot.FullName
    }
}

Add-StepResult -Step 'promotion' -Status 'pass' -Detail "release refreshed at $ReleaseRoot"

Write-Host ''
Write-Host 'Build summary' -ForegroundColor Cyan
$script:StepResults | Format-Table -AutoSize | Out-String | Write-Host

$pendingSteps = @($script:StepResults | Where-Object { $_.Status -eq 'pending' })
if ($pendingSteps.Count -gt 0) {
    Write-Host ''
    Write-Host ("{0} step(s) are PENDING and were NOT verified: {1}" -f $pendingSteps.Count, (($pendingSteps | ForEach-Object { $_.Step }) -join ', ')) -ForegroundColor Yellow
    Write-Host 'This is not a full M0 pass; the pending gates remain open.' -ForegroundColor Yellow
}

$releaseExe = Get-ReleaseExecutable
Write-Host "Release executable: $releaseExe"

if ($Run) {
    Write-Step "Launching the new GUI"
    Start-Process -FilePath $releaseExe -WorkingDirectory (Split-Path -Parent $releaseExe) | Out-Null
    Write-Host 'The application was started; the build script returns immediately.'
}

exit 0
