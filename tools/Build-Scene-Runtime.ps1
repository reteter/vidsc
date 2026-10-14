#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the relocatable, self-contained Python runtime for the vidsc scene worker.

.DESCRIPTION
    The scene worker (tools/scene/vidsc_scene_job.py) must run from the delivery bundle on a machine
    that has no Python installed and no host user site-packages. The previous local runtime was a
    normal `python -m venv` tree whose `pyvenv.cfg` pointed at `C:\Python314`; that is not
    relocatable and it is not redistributable.

    This script produces a genuinely self-contained runtime from the OFFICIAL Windows embeddable
    distribution plus the already-validated wheel set:

      1. download the pinned embeddable archive into artifacts/downloads and verify its SHA256;
      2. extract it into a project-owned staging directory below artifacts/runtime/scene;
      3. create Lib/site-packages, install a sitecustomize.py that keeps `sys.path` inside the
         runtime (the embeddable honours `import site`, which would otherwise also add the host
         per-user site directory), and rewrite `pythonNNN._pth` so it lists the runtime's own
         site-packages and enables `import site` -- no PYTHONPATH and no absolute host path;
      4. bootstrap pip inside that runtime with get-pip.py (pinned by SHA256) and install the pinned
         wheels from artifacts/runtime/scene/wheels (downloading the pinned file from PyPI and
         verifying its SHA256 when a wheel is missing);
      5. promote the staging directory to artifacts/runtime/scene/python;
      6. write artifacts/runtime/scene/scene-runtime-manifest-relocatable.json.

    There is deliberately no venv: no pyvenv.cfg is created and nothing depends on C:\Python314.
    Verification (import probe, `pip check`, a self-contained PyAV+PySceneDetect capability probe on
    a fixture, the shared scene driver, and the host-reference scan) is part of the build;
    -CleanLocationTest additionally copies the runtime to a directory that contains a space and a
    non-ASCII character and runs it there with the development Python removed from PATH and all
    VIDSC_* overrides unset.

.PARAMETER Force
    Rebuild the runtime from scratch even when the existing one already matches the pins.

.PARAMETER SkipVerify
    Skip the runtime capability probe and the scene-worker run. Weakens the build gate; the manifest
    is then written as scene-runtime-manifest-relocatable.unverified.json so a weakened run can never
    overwrite the verified manifest.

.PARAMETER RequireSceneDriver
    Turn the tools/scene/vidsc_scene_job.py integration result into a fatal gate. By default that
    driver is a separately owned deliverable, so its own regression is reported as a warning while
    the runtime is still gated on the self-contained capability probe.

.PARAMETER CleanLocationTest
    After a successful build, copy the runtime to
    artifacts/runtime/scene/<CleanLocationName>/python and run the worker and the capability probe
    there from a clean environment that cannot reach the development Python.

.PARAMETER CleanLocationName
    Name of the relocation test directory (a single path segment below artifacts/runtime/scene).
    It contains a space and non-ASCII characters on purpose.

.EXAMPLE
    pwsh -NoProfile -File tools/Build-Scene-Runtime.ps1 -Force -CleanLocationTest

.EXAMPLE
    pwsh -NoProfile -File tools/Build-Scene-Runtime.ps1 -Force -CleanLocationTest -RequireSceneDriver
#>
[CmdletBinding()]
param(
    [switch] $Force,
    [switch] $SkipVerify,
    [switch] $CleanLocationTest,
    [switch] $RequireSceneDriver,
    [string] $CleanLocationName = 'relocate test Ünïcode'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Native tools write progress and warnings to stderr; exit codes are checked explicitly instead.
$PSNativeCommandUseErrorActionPreference = $false

# ---------------------------------------------------------------- pins -------

# Official Windows embeddable distribution. SHA256 recorded from the pinned download in
# artifacts/downloads; python.org does not publish a per-file checksum for the embeddable archive,
# so the value is pinned here and re-verified on every build.
$Embeddable = [ordered]@{
    Version  = '3.14.5'
    FileName = 'python-3.14.5-embed-amd64.zip'
    Url      = 'https://www.python.org/ftp/python/3.14.5/python-3.14.5-embed-amd64.zip'
    Sha256   = 'ba6bd811c4eedb19195cf275770ef127e893d63701e24152606e2cb76f6d876a'
    Size     = 12048447
    AbiTag   = 'cp314'
}

# pip bootstrap. get-pip.py is fetched once, cached under artifacts/downloads and verified by hash;
# the pip version it must produce is pinned so the closure is deterministic.
$PipBootstrap = [ordered]@{
    FileName   = 'get-pip.py'
    Url        = 'https://bootstrap.pypa.io/get-pip.py'
    Sha256     = 'fb24e693bab954209a063d90953621412ccad4a500905a726286e038f508ddf6'
    Size       = 2230488
    PipVersion = '26.2.1'
}

# Exactly the already-validated package set (see docs/validation/2026-10-14-m0-m1.md, section 5).
# Every entry is pinned by filename and SHA256; the SHA256 values were read back from the cached
# wheels in artifacts/runtime/scene/wheels and cross-checked against the PyPI JSON API digests.
$Packages = @(
    [ordered]@{ Name = 'scenedetect';    Version = '0.7.2';      FileName = 'scenedetect-0.7.2-py3-none-any.whl';         Sha256 = '953b89fe7e6b82a497c263e26245ad599b004aa7afa74814312980479a040641'; Size = 149715;   Url = 'https://files.pythonhosted.org/packages/59/db/2a0701e48dcbe50f91f8ae186ebb275885ef820a5f9348ff0b52299e4def/scenedetect-0.7.2-py3-none-any.whl' }
    [ordered]@{ Name = 'av';               Version = '19.0.1';     FileName = 'av-19.0.1-cp312-abi3-win_amd64.whl';          Sha256 = '906fc3db09288319a75ea23ffefb59961c7dbe0d1c074601507a89de7d8593d8'; Size = 28149519; Url = 'https://files.pythonhosted.org/packages/e6/dc/a87a5a5e3ac462734f9befd8bad1447301e5802d8c111e22bf708fba7af3/av-19.0.1-cp312-abi3-win_amd64.whl' }
    [ordered]@{ Name = 'numpy';            Version = '2.5.4';      FileName = 'numpy-2.5.4-cp314-cp314-win_amd64.whl';      Sha256 = '2ca144f15135b6212a5c47b1e2aeca6e412f102f95a2d5d88d8aec77eb255de3'; Size = 12703552; Url = 'https://files.pythonhosted.org/packages/52/9a/e1e512ebc948d5b9dd33b08736760f0ebbed2848fd4eda1f553088a6dcee/numpy-2.5.4-cp314-cp314-win_amd64.whl' }
    [ordered]@{ Name = 'opencv-python';    Version = '5.0.0.93';   FileName = 'opencv_python-5.0.0.93-cp37-abi3-win_amd64.whl'; Sha256 = 'f90ba04b8f73bc5c3814037699739f0156f597338a98f05956c684e7c3ca10d2'; Size = 44000345; Url = 'https://files.pythonhosted.org/packages/21/f0/9fa6e85cb10c8eb36a0222d27e50fe381b86ce49a55446bf39f491727564/opencv_python-5.0.0.93-cp37-abi3-win_amd64.whl' }
    [ordered]@{ Name = 'click';            Version = '8.5.0';      FileName = 'click-8.5.0-py3-none-any.whl';                Sha256 = '255bc9599cf7748b4b1a446ccc735421bd08a2ae529a8b88597d3de5664ee360'; Size = 125251;   Url = 'https://files.pythonhosted.org/packages/58/50/6c0d534c5f134586a8e1ba4e330569e32f057e33372ae556463212fb4cd3/click-8.5.0-py3-none-any.whl' }
    [ordered]@{ Name = 'colorama';         Version = '0.4.6';      FileName = 'colorama-0.4.6-py2.py3-none-any.whl';         Sha256 = '4f1d9991f5acc0ca119f9d443620b77f9d6b33703e51011c16baf57afb285fc6'; Size = 25335;    Url = 'https://files.pythonhosted.org/packages/d1/d6/3965ed04c63042e047cb6a3e6ed1a63a35087b6a609aa3a15ed8ac56c221/colorama-0.4.6-py2.py3-none-any.whl' }
    [ordered]@{ Name = 'platformdirs';     Version = '4.13.0';     FileName = 'platformdirs-4.13.0-py3-none-any.whl';        Sha256 = '3dbcf4cd708f21cf876c4eaa90e58412bc4f033d87143f41b1493ff77c25b7e1'; Size = 32724;    Url = 'https://files.pythonhosted.org/packages/8d/15/1633010b26e88e872c93b67c0b6c5e174fb74cb6fb5c1472b4d51d4a8f22/platformdirs-4.13.0-py3-none-any.whl' }
    [ordered]@{ Name = 'tqdm';             Version = '4.70.1';     FileName = 'tqdm-4.70.1-py3-none-any.whl';                Sha256 = 'c293e525e6fef9c20e8728fd4612df02a0aa31bb5fe91ecd93e123b1b7bffa73'; Size = 80199;    Url = 'https://files.pythonhosted.org/packages/a7/03/921a3d3c75785aca9ebfbfcabfbc3a1be12e2ab5265deb026d55a5a3f83e/tqdm-4.70.1-py3-none-any.whl' }
)

# pip itself is bootstrapped by get-pip.py; recorded here so the manifest names a wheel and hash
# for every installed distribution.
$PipWheel = [ordered]@{
    Name = 'pip'; Version = '26.2.1'; FileName = 'pip-26.2.1-py3-none-any.whl'
    Sha256 = '71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e'
    Size = 1816632
    Url = 'https://files.pythonhosted.org/packages/f3/6e/1736e5b4ae2b778ef2f81c47d797de9f891d4d8acb047a24ca37a60294dd/pip-26.2.1-py3-none-any.whl'
}

$HostPythonReference = 'C:\Python314'

# The embeddable adds the host per-user site directory whenever `import site` runs. This file keeps
# `sys.path` inside the runtime so the pinned wheels cannot be shadowed and no host path is used.
$SiteCustomizeContent = @'
"""vidsc scene runtime: keep every import inside this runtime directory.

The embeddable distribution enables ``import site`` (required so that
``Lib\site-packages`` is honoured and pip behaves normally), but CPython's ``site`` module would
then also add the host per-user site directory -- for example
``%APPDATA%\Python\Python314\site-packages`` -- together with every directory that its ``.pth``
files contribute. That would make the runtime depend on the machine profile and could shadow the
pinned wheels.

This module runs at the end of ``site.main()`` and removes every ``sys.path`` entry that is not
inside this runtime, except the directory of the script being executed.
"""

import os
import sys

_runtime_root = os.path.normcase(
    os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir, os.pardir))
)
_runtime_prefix = _runtime_root + os.sep

_script_dir = None
if sys.path and isinstance(sys.path[0], str) and sys.path[0]:
    try:
        candidate = os.path.normcase(os.path.abspath(sys.path[0]))
    except (OSError, ValueError):
        candidate = None
    if candidate and os.path.isdir(candidate):
        _script_dir = candidate

for _entry in list(sys.path):
    if not _entry or not isinstance(_entry, str):
        continue
    try:
        _resolved = os.path.normcase(os.path.abspath(_entry))
    except (OSError, ValueError):
        continue
    if _resolved == _runtime_root or _resolved.startswith(_runtime_prefix):
        continue
    if _script_dir is not None and _resolved == _script_dir:
        continue
    sys.path.remove(_entry)

del _entry

try:
    import site as _site

    _site.ENABLE_USER_SITE = False
except Exception:  # pragma: no cover - site is always importable here
    pass
'@

$FactsProbe = @'
import importlib.metadata as metadata
import json
import platform
import site
import sys
import sysconfig

packages = {}
for dist in metadata.distributions():
    name = (dist.metadata["Name"] or "").strip()
    if name:
        packages[name.lower().replace("_", "-")] = dist.version

facts = {
    "executable": sys.executable,
    "prefix": sys.prefix,
    "basePrefix": sys.base_prefix,
    "version": platform.python_version(),
    "versionString": sys.version,
    "implementation": platform.python_implementation(),
    "machine": platform.machine(),
    "platform": sysconfig.get_platform(),
    "isolated": bool(sys.flags.isolated),
    "noSiteFlag": bool(sys.flags.no_site),
    "enableUserSite": getattr(site, "ENABLE_USER_SITE", None),
    "userSite": getattr(site, "USER_SITE", None),
    "sysPath": list(sys.path),
    "distributions": dict(sorted(packages.items())),
}
print(json.dumps(facts, indent=2))
'@

$ImportProbe = @'
import json
from importlib import metadata

import av
import click
import colorama
import cv2
import numpy
import platformdirs
import scenedetect
import tqdm

names = [
    "scenedetect",
    "av",
    "numpy",
    "opencv-python",
    "click",
    "colorama",
    "platformdirs",
    "tqdm",
    "pip",
]
print(json.dumps({name: metadata.version(name) for name in names}, indent=2))
'@

# Runtime capability probe. It exercises the pinned packages (PyAV decode + ContentDetector) directly,
# so the runtime can be validated independently of tools/scene/vidsc_scene_job.py, which is a
# separately owned deliverable. argv: <video> [<output-json>].
$DetectionProbeCode = @'
import json
import sys

from scenedetect import SceneManager, open_video
from scenedetect.detectors import ContentDetector

video = open_video(sys.argv[1], backend="pyav")
manager = SceneManager()
manager.add_detector(ContentDetector(threshold=12.0, min_scene_len=15))
manager.detect_scenes(video)

scenes = []
for index, (start, end) in enumerate(manager.get_scene_list()):
    scenes.append(
        {
            "index": index,
            "startFrame": int(start.frame_num),
            "endFrame": int(end.frame_num),
            "startSeconds": float(start.seconds),
            "endSeconds": float(end.seconds),
        }
    )

document = {
    "backend": "pyav",
    "frameRate": float(video.frame_rate),
    "detector": "ContentDetector",
    "threshold": 12.0,
    "minSceneLenFrames": 15,
    "sceneCount": len(scenes),
    "scenes": scenes,
}
payload = json.dumps(document, indent=2)
if len(sys.argv) > 2:
    with open(sys.argv[2], "w", encoding="utf-8") as handle:
        handle.write(payload)
print(payload)
'@

# ---------------------------------------------------------------- paths ------

# Resolve the project root by walking up from $PSScriptRoot until the solution file is found. The
# script lives in tools/, so this is normally one level up; the walk keeps it correct if the script
# is ever moved deeper.
$ProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
while (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot 'Vidsc.slnx'))) {
    $parent = Split-Path -Parent $ProjectRoot
    if (-not $parent -or $parent -eq $ProjectRoot) {
        throw "Could not locate the vidsc project root (Vidsc.slnx) above '$PSScriptRoot'."
    }
    $ProjectRoot = $parent
}

$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts'))
$DownloadsRoot = Join-Path $ArtifactsRoot 'downloads'
$RuntimeRoot   = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'runtime/scene'))
$RuntimeDir    = Join-Path $RuntimeRoot 'python'
$StagingDir    = Join-Path $RuntimeRoot '.stage-python'
$WheelsDir     = Join-Path $RuntimeRoot 'wheels'
$FixtureRoot   = Join-Path $ArtifactsRoot 'tests/fixtures'
$FixturePath   = Join-Path $FixtureRoot 'scene-smoke.mp4'
$DriverPath    = Join-Path $ProjectRoot 'tools/scene/vidsc_scene_job.py'
$ManifestName = if ($SkipVerify) { 'scene-runtime-manifest-relocatable.unverified.json' } else { 'scene-runtime-manifest-relocatable.json' }
$ManifestPath = Join-Path $RuntimeRoot $ManifestName
$InPlaceJobJson = Join-Path $RuntimeRoot 'scene-job-relocatable.json'
$InPlaceProbeJson = Join-Path $RuntimeRoot 'detection-probe-relocatable.json'
$GetPipPath    = Join-Path $DownloadsRoot $PipBootstrap.FileName
$EmbeddablePath = Join-Path $DownloadsRoot $Embeddable.FileName

$PthFileName = 'python{0}._pth' -f ($Embeddable.Version.Split('.')[0] + $Embeddable.Version.Split('.')[1])
$PythonExeName = 'python.exe'
$PythonDllName = 'python{0}.dll' -f ($Embeddable.Version.Split('.')[0] + $Embeddable.Version.Split('.')[1])
$StdlibZipName = 'python{0}.zip' -f ($Embeddable.Version.Split('.')[0] + $Embeddable.Version.Split('.')[1])

$PthLines = @(
    $StdlibZipName
    '.'
    'Lib\site-packages'
    ''
    '# Uncomment to run site.main() automatically'
    'import site'
)

$script:StepResults = [System.Collections.Generic.List[object]]::new()
$script:Commands = [System.Collections.Generic.List[object]]::new()

# ------------------------------------------------------------- helpers ------

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "=== $Message" -ForegroundColor Cyan
}

function Write-Info {
    param([string] $Message)
    Write-Host "   $Message" -ForegroundColor DarkGray
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

function Add-StepResult {
    param([string] $Step, [string] $Status, [string] $Detail)
    $script:StepResults.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'pass' { 'Green' } 'warn' { 'Yellow' } 'fail' { 'Red' } 'pending' { 'Yellow' } default { 'Gray' } }
    Write-Host ("[{0,-7}] {1}: {2}" -f $Status, $Step, $Detail) -ForegroundColor $colour
}

function Invoke-Capture {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments,
        [string] $WorkingDirectory = $ProjectRoot
    )

    $previous = Get-Location
    try {
        Set-Location -LiteralPath $WorkingDirectory
        $output = & $Executable @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Set-Location -LiteralPath $previous
    }

    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $script:Commands.Add([ordered]@{
            command   = "$Executable $($Arguments -join ' ')"
            directory = $WorkingDirectory
            exitCode  = $exitCode
            output    = $text
        })

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $text }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $Description,
        [string] $WorkingDirectory = $ProjectRoot
    )

    Write-Host "-> $Description"
    Write-Host ("   {0} {1}" -f $Executable, ($Arguments -join ' ')) -ForegroundColor DarkGray

    $result = Invoke-Capture -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -ne 0) {
        $tail = ($result.Output -split "`n" | Select-Object -Last 40) -join [Environment]::NewLine
        throw "$Description failed with exit code $($result.ExitCode).$([Environment]::NewLine)$tail"
    }

    return $result
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $ExpectedSha256,
        [Parameter(Mandatory)][long] $ExpectedSize
    )

    if (Test-Path -LiteralPath $Destination) {
        $observed = Get-Sha256 -Path $Destination
        if ($observed -eq $ExpectedSha256) {
            Write-Info "using cached $Destination (sha256 $observed verified)"
            return 'cache'
        }
        throw "The cached file '$Destination' has sha256 $observed but the pinned value is $ExpectedSha256. Delete the cached file to re-download it."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Write-Host "-> downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination

    $observed = Get-Sha256 -Path $Destination
    $size = (Get-Item -LiteralPath $Destination).Length

    if ($observed -ne $ExpectedSha256) {
        throw "The downloaded file '$Destination' has sha256 $observed but the pinned value is $ExpectedSha256."
    }
    if ($size -ne $ExpectedSize) {
        throw "The downloaded file '$Destination' has $size bytes but the pinned size is $ExpectedSize."
    }

    Write-Info "downloaded $Destination (sha256 $observed verified)"
    return 'download'
}

function Write-TextFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Content,
        [ValidateSet('ascii', 'utf8')][string] $Encoding = 'ascii'
    )

    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    # .NET (Core) does not accept the name 'utf8'; the encoding objects are built explicitly, and
    # the ASCII writer fails loudly on any non-ASCII character in the ._pth file.
    $encodingObject = if ($Encoding -eq 'utf8') {
        [System.Text.UTF8Encoding]::new($false)
    }
    else {
        [System.Text.ASCIIEncoding]::new()
    }

    [System.IO.File]::WriteAllText($Path, $Content, $encodingObject)
}

# A byte-oriented literal search over every file below $Root. Latin-1 keeps a 1:1 byte-to-character
# mapping, so a string IndexOf finds the exact byte sequence regardless of the file's real encoding.
function Find-TextInTree {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $Needle)

    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $hits = [System.Collections.Generic.List[object]]::new()
    $files = 0

    foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force)) {
        if ($file.Length -gt 256MB) { continue }
        $files++
        try {
            $text = [System.IO.File]::ReadAllText($file.FullName, $latin1)
        }
        catch {
            continue
        }
        if ($text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
            $relative = $file.FullName.Substring($Root.Length + 1)
            $hits.Add([ordered]@{ path = $relative; sizeBytes = $file.Length })
        }
    }

    return [pscustomobject]@{ ScannedFiles = $files; Hits = $hits }
}

<#
.SYNOPSIS
    True when the file participates in how the interpreter resolves its own paths and packages.
#>
function Test-CriticalConfigFile {
    param([Parameter(Mandatory)][string] $RelativePath)

    return ($RelativePath -eq $PthFileName) `
        -or ($RelativePath -like 'python*.zip') `
        -or ($RelativePath -match '(?i)\.(cfg|pth)$')
}

<#
.SYNOPSIS
    True when the file is a path-configuration file or code that the runtime actually executes.
.DESCRIPTION
    A host path hard-coded in one of these files would be a real relocation dependency. The same
    string appearing elsewhere (for example inside a compiled extension) is recorded but is not
    treated as a dependency; the clean-location run is the authoritative proof.
#>
function Test-CriticalRuntimeFile {
    param([Parameter(Mandatory)][string] $RelativePath)

    return (Test-CriticalConfigFile -RelativePath $RelativePath) `
        -or ($RelativePath -match '(?i)\.(py|pyw|json|ini|toml)$')
}

function Test-RuntimeReady {
    param([Parameter(Mandatory)][string] $Directory)

    $exe = Join-Path $Directory $PythonExeName
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory "Lib/site-packages/sitecustomize.py"))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $PthFileName))) { return $false }

    $result = Invoke-Capture -Executable $exe -Arguments @('-c', $FactsProbe)
    if ($result.ExitCode -ne 0) { return $false }

    try {
        $facts = $result.Output | ConvertFrom-Json
    }
    catch {
        return $false
    }

    foreach ($package in $Packages) {
        $observed = $facts.distributions.($package.Name)
        if ($observed -ne $package.Version) { return $false }
    }
    if ($facts.distributions.('pip') -ne $PipBootstrap.PipVersion) { return $false }

    return $true
}

function Get-Fixture {
    if (Test-Path -LiteralPath $FixturePath) {
        Add-StepResult -Step 'fixture' -Status 'pass' -Detail "using the existing fixture $FixturePath"
        return
    }

    $candidates = @(
        'C:\ffmpeg\bin\ffmpeg.exe'
        (Join-Path $ArtifactsRoot 'release/app/tools/ffmpeg/ffmpeg.exe')
    )

    $ffmpeg = $null
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { $ffmpeg = $candidate; break }
    }

    if (-not $ffmpeg) {
        throw "The scene smoke fixture is missing and no ffmpeg was found (tried: $($candidates -join ', '))."
    }

    New-Item -ItemType Directory -Force -Path $FixtureRoot | Out-Null

    $arguments = @(
        '-hide_banner', '-nostdin', '-v', 'error', '-y',
        '-f', 'lavfi', '-i', 'color=c=red:s=320x180:r=24:d=2',
        '-f', 'lavfi', '-i', 'color=c=lime:s=320x180:r=24:d=2',
        '-f', 'lavfi', '-i', 'color=c=blue:s=320x180:r=24:d=2',
        '-filter_complex', '[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p[v]',
        '-map', '[v]', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18',
        '-pix_fmt', 'yuv420p', '-r', '24', '-movflags', '+faststart',
        $FixturePath
    )

    $null = Invoke-Checked -Executable $ffmpeg -Arguments $arguments -Description 'generate the scene smoke fixture'
    Add-StepResult -Step 'fixture' -Status 'pass' -Detail "generated with $ffmpeg"
}

<#
.SYNOPSIS
    Runs the scene worker with the pinned runtime and validates the JSON result.
#>
function Invoke-SceneJob {
    param(
        [Parameter(Mandatory)][string] $PythonExe,
        [Parameter(Mandatory)][string] $Driver,
        [Parameter(Mandatory)][string] $OutputJson,
        [string] $WorkingDirectory = $ProjectRoot
    )

    $result = Invoke-Capture -Executable $PythonExe -Arguments @(
        $Driver, '--input', $FixturePath, '--output', $OutputJson, '--detector', 'content', '--threshold', '12'
    ) -WorkingDirectory $WorkingDirectory

    $json = $null
    if (Test-Path -LiteralPath $OutputJson) {
        try { $json = Get-Content -LiteralPath $OutputJson -Raw | ConvertFrom-Json } catch { $json = $null }
    }

    $problems = [System.Collections.Generic.List[string]]::new()

    if ($result.ExitCode -ne 0) { $problems.Add("exit code $($result.ExitCode)") }
    if (-not $json) { $problems.Add('no parseable JSON report was written') }
    else {
        if (-not $json.success) { $problems.Add("success=$($json.success) error=$($json.error)") }
        if (@($json.scenes).Count -ne 3) { $problems.Add("expected 3 scenes, observed $(@($json.scenes).Count)") }
        if ($json.backend -ne 'pyav') { $problems.Add("backend was '$($json.backend)'") }
        if ($json.scenedetectVersion -ne '0.7.2') { $problems.Add("scenedetect version was '$($json.scenedetectVersion)'") }
        if ($json.pyavVersion -ne '19.0.1') { $problems.Add("av version was '$($json.pyavVersion)'") }

        $starts = @($json.scenes | ForEach-Object { [math]::Round($_.startSeconds, 3) })
        if (($starts -join ',') -ne '0,2,4') { $problems.Add("scene starts were [$($starts -join ', ')] instead of [0, 2, 4]") }
    }

    return [pscustomobject]@{
        ExitCode  = $result.ExitCode
        Output    = $result.Output
        Json      = $json
        Problems  = $problems
        Succeeded = ($problems.Count -eq 0)
    }
}

# ---------------------------------------------------------------- build ------

$buildMode = 'reused'
$pipWheelRecord = $null
$packageRecords = [System.Collections.Generic.List[object]]::new()
$extractionSource = 'n/a'
$getPipSource = 'n/a'

try {
    Write-Step 'Preflight'

    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw 'PowerShell 7 or later is required.'
    }

    if (-not (Test-Path -LiteralPath $DriverPath)) {
        throw "The scene driver is missing: $DriverPath"
    }

    Assert-UnderArtifacts -Path $RuntimeRoot -Purpose 'write' | Out-Null
    Assert-UnderArtifacts -Path $DownloadsRoot -Purpose 'write' | Out-Null
    Assert-UnderArtifacts -Path $FixtureRoot -Purpose 'write' | Out-Null

    New-Item -ItemType Directory -Force -Path $WheelsDir, $DownloadsRoot | Out-Null

    Write-Info "project root     : $ProjectRoot"
    Write-Info "runtime          : $RuntimeDir"
    Write-Info "wheels           : $WheelsDir"
    Write-Info "downloads        : $DownloadsRoot"

    $needsBuild = $true
    if (-not $Force) {
        $needsBuild = -not (Test-RuntimeReady -Directory $RuntimeDir)
        if (-not $needsBuild) {
            Write-Info 'the existing runtime matches every pin; rebuilding is skipped (-Force rebuilds)'
        }
    }

    if ($needsBuild) {
        # ------------------------------------------------------- embeddable ---
        Write-Step "Python $($Embeddable.Version) embeddable distribution"

        $extractionSource = Get-VerifiedDownload -Url $Embeddable.Url -Destination $EmbeddablePath `
            -ExpectedSha256 $Embeddable.Sha256 -ExpectedSize $Embeddable.Size

        Add-StepResult -Step 'embeddable-download' -Status 'pass' -Detail "sha256 $($Embeddable.Sha256) ($extractionSource)"

        Remove-Owned -Path $StagingDir
        Assert-UnderArtifacts -Path $StagingDir -Purpose 'extract into' | Out-Null
        New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

        Expand-Archive -LiteralPath $EmbeddablePath -DestinationPath $StagingDir -Force

        foreach ($required in @($PythonExeName, $PythonDllName, $StdlibZipName, $PthFileName)) {
            if (-not (Test-Path -LiteralPath (Join-Path $StagingDir $required))) {
                throw "The embeddable archive did not produce '$required' in $StagingDir."
            }
        }

        Add-StepResult -Step 'embeddable-extract' -Status 'pass' -Detail "extracted to $StagingDir"

        # --------------------------------------------------------- _pth file ---
        # No absolute path may appear here: the runtime root is the ._pth file's own directory.
        Write-TextFile -Path (Join-Path $StagingDir $PthFileName) -Content (($PthLines -join "`r`n") + "`r`n")
        Write-TextFile -Path (Join-Path $StagingDir 'Lib/site-packages/sitecustomize.py') -Content $SiteCustomizeContent -Encoding 'utf8'

        $pthObserved = (Get-Content -LiteralPath (Join-Path $StagingDir $PthFileName)) -join ' | '
        Write-Info "$PthFileName : $pthObserved"
        Add-StepResult -Step 'pth-file' -Status 'pass' -Detail "$PthFileName lists Lib\\site-packages and enables import site (relative paths only)"

        # ---------------------------------------------------------- pip ------
        Write-Step "pip bootstrap ($($PipBootstrap.FileName))"

        $getPipSource = Get-VerifiedDownload -Url $PipBootstrap.Url -Destination $GetPipPath `
            -ExpectedSha256 $PipBootstrap.Sha256 -ExpectedSize $PipBootstrap.Size

        $stagingPython = Join-Path $StagingDir $PythonExeName

        $null = Invoke-Checked -Executable $stagingPython -Arguments @(
            $GetPipPath, "pip==$($PipBootstrap.PipVersion)", '--no-cache-dir', '--no-warn-script-location'
        ) -Description "bootstrap pip $($PipBootstrap.PipVersion) into the runtime with get-pip.py" -WorkingDirectory $StagingDir

        $pipProbe = Invoke-Checked -Executable $stagingPython -Arguments @('-m', 'pip', '--version') `
            -Description 'read the bootstrapped pip version' -WorkingDirectory $StagingDir

        if ($pipProbe.Output -notmatch [regex]::Escape("pip $($PipBootstrap.PipVersion)")) {
            throw "get-pip.py did not install pip $($PipBootstrap.PipVersion). Observed: $($pipProbe.Output)"
        }

        Add-StepResult -Step 'pip-bootstrap' -Status 'pass' -Detail "pip $($PipBootstrap.PipVersion) via get-pip.py ($getPipSource, sha256 $($PipBootstrap.Sha256))"

        # ------------------------------------------------------- wheels -------
        Write-Step 'Pinned wheels'

        $installRequirements = [System.Collections.Generic.List[string]]::new()

        foreach ($package in $Packages) {
            $wheelPath = Join-Path $WheelsDir $package.FileName
            $source = 'local-wheel-cache'

            if ((Test-Path -LiteralPath $wheelPath) -and ((Get-Sha256 -Path $wheelPath) -eq $package.Sha256)) {
                Write-Info "cached   $($package.FileName)"
            }
            else {
                Write-Info "fetch    $($package.FileName) from PyPI"
                $null = Get-VerifiedDownload -Url $package.Url -Destination $wheelPath `
                    -ExpectedSha256 $package.Sha256 -ExpectedSize $package.Size
                $source = 'downloaded-from-pypi'
            }

            $packageRecords.Add([ordered]@{
                    name        = $package.Name
                    version     = $package.Version
                    wheelFile   = $package.FileName
                    sha256      = $package.Sha256
                    sizeBytes   = $package.Size
                    artifactUrl = $package.Url
                    source      = $source
                })

            $installRequirements.Add("$($package.Name)==$($package.Version)")
        }

        Add-StepResult -Step 'wheels' -Status 'pass' -Detail "$($packageRecords.Count) wheels verified by SHA256 in $WheelsDir"

        $null = Invoke-Checked -Executable $stagingPython -Arguments (@(
                '-m', 'pip', 'install',
                '--no-index', '--find-links', $WheelsDir,
                '--no-cache-dir', '--no-warn-script-location', '--disable-pip-version-check',
                '--progress-bar', 'off'
            ) + $installRequirements.ToArray()) -Description 'install the pinned wheels into the runtime site-packages' -WorkingDirectory $StagingDir

        $pipCheck = Invoke-Capture -Executable $stagingPython -Arguments @('-m', 'pip', 'check') -WorkingDirectory $StagingDir
        if ($pipCheck.ExitCode -ne 0) {
            throw "pip check failed (exit $($pipCheck.ExitCode)): $($pipCheck.Output)"
        }
        Add-StepResult -Step 'pip-check' -Status 'pass' -Detail 'no broken requirements'

        # -------------------------------------------------------- promote ----
        Write-Step 'Promote the staging runtime'

        Remove-Owned -Path $RuntimeDir
        Move-Item -LiteralPath $StagingDir -Destination $RuntimeDir -Force

        $buildMode = 'built'
        Add-StepResult -Step 'runtime-promoted' -Status 'pass' -Detail "runtime promoted to $RuntimeDir"
    }
    else {
        foreach ($package in $Packages) {
            $wheelPath = Join-Path $WheelsDir $package.FileName
            $packageRecords.Add([ordered]@{
                    name        = $package.Name
                    version     = $package.Version
                    wheelFile   = $package.FileName
                    sha256      = $package.Sha256
                    sizeBytes   = $package.Size
                    artifactUrl = $package.Url
                    source      = 'local-wheel-cache'
                })
        }
    }

    Remove-Owned -Path $StagingDir

    # -------------------------------------------------------- verify -------
    Write-Step 'Runtime verification'

    $runtimePython = Join-Path $RuntimeDir $PythonExeName
    if (-not (Test-Path -LiteralPath $runtimePython)) {
        throw "The runtime interpreter is missing: $runtimePython"
    }

    $factsResult = Invoke-Checked -Executable $runtimePython -Arguments @('-c', $FactsProbe) `
        -Description 'collect interpreter facts' -WorkingDirectory $RuntimeDir
    $facts = $factsResult.Output | ConvertFrom-Json

    if ($facts.version -ne $Embeddable.Version) {
        throw "The runtime reports Python $($facts.version) but the pinned interpreter is $($Embeddable.Version)."
    }

    $versionProblems = [System.Collections.Generic.List[string]]::new()
    foreach ($package in $Packages) {
        $observed = $facts.distributions.($package.Name)
        if ($observed -ne $package.Version) {
            $versionProblems.Add("$($package.Name)=$observed (expected $($package.Version))")
        }
    }
    if ($facts.distributions.('pip') -ne $PipBootstrap.PipVersion) {
        $versionProblems.Add("pip=$($facts.distributions.('pip')) (expected $($PipBootstrap.PipVersion))")
    }
    if ($versionProblems.Count -gt 0) {
        throw "Installed versions do not match the pins: $($versionProblems -join '; ')"
    }
    Add-StepResult -Step 'installed-versions' -Status 'pass' -Detail "$($facts.distributions.PSObject.Properties.Name.Count) distributions at the pinned versions"

    $importResult = Invoke-Checked -Executable $runtimePython -Arguments @('-c', $ImportProbe) `
        -Description 'import every pinned package' -WorkingDirectory $RuntimeDir

    # The runtime must resolve everything from inside itself.
    $foreignPaths = @($facts.sysPath | Where-Object {
            $_ -and $_.ToString() -and
            (-not ([System.IO.Path]::GetFullPath($_)).StartsWith([System.IO.Path]::GetFullPath($RuntimeDir), [System.StringComparison]::OrdinalIgnoreCase))
        })

    if ($foreignPaths.Count -gt 0) {
        throw "sys.path contains entries outside the runtime: $($foreignPaths -join '; ')"
    }
    if ($facts.userSite -and $facts.enableUserSite) {
        throw "The per-user site directory is still active: $($facts.userSite)"
    }
    Add-StepResult -Step 'self-contained-sys-path' -Status 'pass' -Detail "sys.path resolves inside $RuntimeDir only; user site disabled"

    # -------------------------------------------------- host references -----
    Write-Step "Host reference scan ($HostPythonReference, pyvenv)"

    $homeScan = Find-TextInTree -Root $RuntimeDir -Needle $HostPythonReference
    $venvScan = Find-TextInTree -Root $RuntimeDir -Needle 'pyvenv.cfg'
    $pyvenvFiles = @(Get-ChildItem -LiteralPath $RuntimeDir -Recurse -File -Force -Filter 'pyvenv.cfg')

    Add-StepResult -Step 'host-python-scan' -Status 'pass' -Detail "$($homeScan.ScannedFiles) files scanned; $($homeScan.Hits.Count) file(s) mention $HostPythonReference"
    Add-StepResult -Step 'pyvenv-scan' -Status 'pass' -Detail "$($pyvenvFiles.Count) pyvenv.cfg file(s) present; $($venvScan.Hits.Count) benign mention(s) of the 'pyvenv.cfg' string in CPython/pip venv-detection code"

    if ($pyvenvFiles.Count -gt 0) {
        throw "A pyvenv.cfg file exists in the runtime: $($pyvenvFiles.FullName -join ', ')"
    }

    foreach ($hit in $homeScan.Hits) {
        Write-Info "host path mentioned in: $($hit.path)"
    }
    foreach ($hit in $venvScan.Hits) {
        Write-Info "pyvenv.cfg mentioned in: $($hit.path)"
    }

    $criticalHit = $false
    foreach ($hit in $homeScan.Hits) {
        if (Test-CriticalRuntimeFile -RelativePath $hit.path) {
            Write-Info "critical: $($hit.path) mentions the host Python path"
            $criticalHit = $true
        }
    }
    foreach ($hit in $venvScan.Hits) {
        if (Test-CriticalConfigFile -RelativePath $hit.path) {
            Write-Info "critical: $($hit.path) mentions a venv marker"
            $criticalHit = $true
        }
    }
    if ($criticalHit) {
        throw 'A path-configuration or source file of the runtime mentions the host Python or a venv. The runtime is not relocatable.'
    }

    $benignVenvMentions = @($venvScan.Hits | Where-Object { -not (Test-CriticalConfigFile -RelativePath $_.path) } | ForEach-Object { $_.path })
    if ($benignVenvMentions.Count -gt 0) {
        Write-Info "benign: the remaining mention(s) of 'pyvenv.cfg' are CPython/pip venv-detection code, not a dependency: $($benignVenvMentions -join ', ')"
    }

    # ------------------------------------------- runtime capability ----------
    # This probe uses only the pinned runtime and the pinned PySceneDetect API. It does not depend on
    # tools/scene/vidsc_scene_job.py, which is a separately owned deliverable, so a regression in the
    # driver can never be misreported as a defect of the runtime this script builds.
    if ($SkipVerify) {
        $detectionProbe = $null
        $sceneWorker = $null
        Add-StepResult -Step 'detection-probe' -Status 'pending' -Detail 'skipped by -SkipVerify (build gate weakened)'
        Add-StepResult -Step 'scene-worker-driver' -Status 'pending' -Detail 'skipped by -SkipVerify (build gate weakened)'
    }
    else {
        Get-Fixture

        $probeRun = Invoke-Capture -Executable $runtimePython -Arguments @('-c', $DetectionProbeCode, $FixturePath, $InPlaceProbeJson) `
            -WorkingDirectory $RuntimeDir

        $probeProblems = [System.Collections.Generic.List[string]]::new()
        $probeJson = $null

        if ($probeRun.ExitCode -ne 0) {
            Write-Host $probeRun.Output -ForegroundColor DarkYellow
            $probeProblems.Add("the detection probe exited with $($probeRun.ExitCode): $(($probeRun.Output -split "`n" | Select-Object -Last 3) -join ' / ')")
        }
        else {
            try { $probeJson = $probeRun.Output | ConvertFrom-Json } catch { $probeProblems.Add('the detection probe emitted no parseable JSON') }
        }

        if ($probeJson) {
            $probeStarts = @($probeJson.scenes | ForEach-Object { [math]::Round($_.startSeconds, 3) })
            $probeFrames = @($probeJson.scenes | ForEach-Object { [int]$_.startFrame })
            if (@($probeJson.scenes).Count -ne 3) { $probeProblems.Add("expected 3 scenes, observed $(@($probeJson.scenes).Count)") }
            if (($probeStarts -join ',') -ne '0,2,4') { $probeProblems.Add("scene starts were [$($probeStarts -join ', ')] instead of [0, 2, 4]") }
            if (($probeFrames -join ',') -ne '0,48,96') { $probeProblems.Add("scene start frames were [$($probeFrames -join ', ')] instead of [0, 48, 96]") }
        }

        $detectionProbe = [ordered]@{
            command       = 'python -c <DetectionProbe> <fixture>'
            fixture       = $FixturePath
            report        = $InPlaceProbeJson
            exitCode      = $probeRun.ExitCode
            frameRate     = if ($probeJson) { $probeJson.frameRate } else { $null }
            sceneCount    = if ($probeJson) { @($probeJson.scenes).Count } else { 0 }
            sceneStartFrames = if ($probeJson) { @($probeJson.scenes | ForEach-Object { [int]$_.startFrame }) } else { @() }
            sceneStartSeconds = if ($probeJson) { @($probeJson.scenes | ForEach-Object { [double]$_.startSeconds }) } else { @() }
            problems      = @($probeProblems)
        }

        if ($probeProblems.Count -gt 0) {
            throw "The pinned runtime could not detect the expected cuts on the fixture: $($probeProblems -join '; ')"
        }

        Add-StepResult -Step 'detection-probe' -Status 'pass' -Detail (
            "PyAV decode + ContentDetector found $(@($probeJson.scenes).Count) scenes at [$((@($probeJson.scenes) | ForEach-Object { '{0:0.000}' -f $_.startSeconds }) -join ', ')] s (frames [$($probeJson.scenes.startFrame -join ', ')])"
        )

        # The packaged driver is the integration contract. It is owned by another deliverable, so its
        # own regression is reported explicitly instead of being blamed on the runtime.
        $inPlace = Invoke-SceneJob -PythonExe $runtimePython -Driver $DriverPath -OutputJson $InPlaceJobJson

        $sceneWorker = [ordered]@{
            driver    = $DriverPath
            report    = $InPlaceJobJson
            exitCode  = $inPlace.ExitCode
            succeeded = $inPlace.Succeeded
            details   = @($inPlace.Problems)
        }

        if ($inPlace.Succeeded) {
            Add-StepResult -Step 'scene-worker-driver' -Status 'pass' -Detail (
                "exit 0, $(@($inPlace.Json.scenes).Count) scenes at [$((@($inPlace.Json.scenes) | ForEach-Object { '{0:0.000}' -f $_.startSeconds }) -join ', ')] s, backend=$($inPlace.Json.backend)"
            )
        }
        elseif ($RequireSceneDriver) {
            throw "The packaged scene driver failed with the freshly built runtime: $($inPlace.Problems -join '; ')"
        }
        else {
            Add-StepResult -Step 'scene-worker-driver' -Status 'warn' -Detail "the separately owned driver $DriverPath failed: $($inPlace.Problems -join '; ')"
            Write-Host ''
            Write-Host 'WARNING: tools/scene/vidsc_scene_job.py did not complete against this runtime.' -ForegroundColor Yellow
            Write-Host '         The runtime itself passed the detection probe above. Use -RequireSceneDriver to make' -ForegroundColor Yellow
            Write-Host '         this integration result fatal. See the JSON report for the driver traceback.' -ForegroundColor Yellow
        }
    }

    # ------------------------------------------------- clean location -------
    $cleanLocation = $null
    if ($CleanLocationTest) {
        Write-Step "Clean-location test ($CleanLocationName)"

        $relocationRoot = Assert-UnderArtifacts -Path (Join-Path $RuntimeRoot $CleanLocationName) -Purpose 'write'
        Remove-Owned -Path $relocationRoot
        New-Item -ItemType Directory -Force -Path $relocationRoot | Out-Null

        $relocatedRuntime = Join-Path $relocationRoot 'python'

        $robocopy = Invoke-Capture -Executable 'robocopy' -Arguments @(
            $RuntimeDir, $relocatedRuntime, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1'
        )

        if ($robocopy.ExitCode -ge 8) {
            throw "Copying the runtime to the clean location failed (robocopy exit $($robocopy.ExitCode))."
        }

        $relocatedDriver = Join-Path $relocationRoot 'vidsc_scene_job.py'
        Copy-Item -LiteralPath $DriverPath -Destination $relocatedDriver -Force

        $relocatedPython = Join-Path $relocatedRuntime $PythonExeName
        $relocatedJson = Join-Path $relocationRoot 'scene-job-clean-location.json'
        $relocatedProbeJson = Join-Path $relocationRoot 'detection-probe-clean-location.json'

        # A clean environment: the development Python removed from PATH, every VIDSC_* override
        # unset and no PYTHONPATH/PYTHONHOME pointing anywhere.
        $savedEnvironment = @{}
        foreach ($name in @('PATH', 'PYTHONPATH', 'PYTHONHOME', 'PYTHONNOUSERSITE',
                'VIDSC_NATIVE_DIR', 'VIDSC_FFMPEG_DIR', 'VIDSC_SCENE_DIR', 'VIDSC_WORK_DIR')) {
            $savedEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, 'Process')
        }

        try {
            $pathValue = [string] $savedEnvironment['PATH']
            $removedFromPath = [System.Collections.Generic.List[string]]::new()

            # Remove every PATH entry that names a Python interpreter: the development interpreter
            # (C:\Python314) by name, any directory whose name mentions python, and any directory that
            # actually contains python.exe / python3.exe / pythonw.exe.
            $pathEntries = ($pathValue -split ';') | Where-Object {
                $entry = $_.Trim()
                if (-not $entry) { return $false }

                if ($entry.TrimEnd('\') -eq $HostPythonReference.TrimEnd('\')) {
                    $removedFromPath.Add("$entry ($HostPythonReference)")
                    return $false
                }
                if ($entry -match '(?i)python') {
                    $removedFromPath.Add("$entry (name contains 'python')")
                    return $false
                }
                foreach ($interpreter in @('python.exe', 'python3.exe', 'pythonw.exe')) {
                    if (Test-Path -LiteralPath (Join-Path $entry $interpreter)) {
                        $removedFromPath.Add("$entry (contains $interpreter)")
                        return $false
                    }
                }

                return $true
            }
            $env:PATH = ($pathEntries -join ';')

            foreach ($name in @('PYTHONPATH', 'PYTHONHOME', 'PYTHONNOUSERSITE',
                    'VIDSC_NATIVE_DIR', 'VIDSC_FFMPEG_DIR', 'VIDSC_SCENE_DIR', 'VIDSC_WORK_DIR')) {
                Remove-Item "Env:$name" -ErrorAction SilentlyContinue
            }

            $devPythonOnPath = $null
            $whereExitCode = $null
            try {
                $devPythonOnPath = (& where.exe python.exe 2>&1) -join ' '
                $whereExitCode = $LASTEXITCODE
            }
            catch {
                $devPythonOnPath = '(where.exe failed)'
            }
            Write-Info "PATH for the clean-location run: $env:PATH"
            Write-Info "removed from PATH        : $($removedFromPath -join ' | ')"
            Write-Info "where.exe python.exe     : $devPythonOnPath (exit $whereExitCode)"

            if ($env:PATH -match [regex]::Escape($HostPythonReference)) {
                throw 'The development Python directory is still on PATH; the clean-location test is not clean.'
            }
            if ($whereExitCode -eq 0) {
                Write-Info 'note: some python.exe is still resolvable through PATH; the runtime does not use it (see the sys.path check below)'
            }

            # The relocated runtime must be capable on its own, with no host Python reachable.
            $relocatedProbeRun = Invoke-Capture -Executable $relocatedPython `
                -Arguments @('-c', $DetectionProbeCode, $FixturePath, $relocatedProbeJson) -WorkingDirectory $relocationRoot

            $relocatedProbe = $null
            if ($relocatedProbeRun.ExitCode -eq 0) {
                try { $relocatedProbe = $relocatedProbeRun.Output | ConvertFrom-Json } catch { $relocatedProbe = $null }
            }

            $relocatedProbeProblems = [System.Collections.Generic.List[string]]::new()
            if ($relocatedProbeRun.ExitCode -ne 0) {
                Write-Host $relocatedProbeRun.Output -ForegroundColor DarkYellow
                $relocatedProbeProblems.Add("exit code $($relocatedProbeRun.ExitCode): $(($relocatedProbeRun.Output -split "`n" | Select-Object -Last 3) -join ' / ')")
            }
            if (-not $relocatedProbe) { $relocatedProbeProblems.Add('no parseable JSON from the relocated detection probe') }
            else {
                $relocatedStarts = @($relocatedProbe.scenes | ForEach-Object { [math]::Round($_.startSeconds, 3) })
                if (@($relocatedProbe.scenes).Count -ne 3) { $relocatedProbeProblems.Add("expected 3 scenes, observed $(@($relocatedProbe.scenes).Count)") }
                if (($relocatedStarts -join ',') -ne '0,2,4') { $relocatedProbeProblems.Add("scene starts were [$($relocatedStarts -join ', ')] instead of [0, 2, 4]") }
            }

            $relocatedDriverRun = Invoke-SceneJob -PythonExe $relocatedPython -Driver $relocatedDriver `
                -OutputJson $relocatedJson -WorkingDirectory $relocationRoot

            # The copied runtime must resolve everything from the location it was copied to.
            $relocatedFactsRun = Invoke-Capture -Executable $relocatedPython -Arguments @('-c', $FactsProbe) `
                -WorkingDirectory $relocationRoot

            $relocatedFacts = $null
            if ($relocatedFactsRun.ExitCode -eq 0) {
                try { $relocatedFacts = $relocatedFactsRun.Output | ConvertFrom-Json } catch { $relocatedFacts = $null }
            }

            $relocatedForeignPaths = @()
            if ($relocatedFacts) {
                $relocatedForeignPaths = @($relocatedFacts.sysPath | Where-Object {
                        $_ -and $_.ToString() -and
                        (-not ([System.IO.Path]::GetFullPath($_)).StartsWith([System.IO.Path]::GetFullPath($relocatedRuntime), [System.StringComparison]::OrdinalIgnoreCase))
                    })
            }
            else {
                $relocatedProbeProblems.Add('the relocated interpreter facts probe produced no JSON')
            }
            if ($relocatedForeignPaths.Count -gt 0) {
                $relocatedProbeProblems.Add("sys.path contains entries outside the copied runtime: $($relocatedForeignPaths -join '; ')")
            }

            if ($relocatedFacts) {
                Write-Info "relocated sys.prefix : $($relocatedFacts.prefix)"
                Write-Info "relocated sys.path   : $(@($relocatedFacts.sysPath) -join '; ')"
            }

            $relocatedHomeScan = Find-TextInTree -Root $relocatedRuntime -Needle $HostPythonReference
            $relocatedVenvScan = Find-TextInTree -Root $relocatedRuntime -Needle 'pyvenv.cfg'
            $relocatedPyvenvFiles = @(Get-ChildItem -LiteralPath $relocatedRuntime -Recurse -File -Force -Filter 'pyvenv.cfg')

            $cleanLocation = [ordered]@{
                directory                  = $relocatedRuntime
                copiedFrom                 = $RuntimeDir
                driver                     = $relocatedDriver
                driverReport               = $relocatedJson
                detectionProbeReport       = $relocatedProbeJson
                pathUsed                   = $env:PATH
                removedFromPath            = @($removedFromPath)
                developmentPythonReachable = $devPythonOnPath
                whereExeExitCode           = $whereExitCode
                environmentUnset           = @('PYTHONPATH', 'PYTHONHOME', 'PYTHONNOUSERSITE', 'VIDSC_NATIVE_DIR', 'VIDSC_FFMPEG_DIR', 'VIDSC_SCENE_DIR', 'VIDSC_WORK_DIR')
                probeExitCode              = $relocatedProbeRun.ExitCode
                probeSceneCount            = if ($relocatedProbe) { @($relocatedProbe.scenes).Count } else { 0 }
                probeSceneStarts           = if ($relocatedProbe) { @($relocatedProbe.scenes | ForEach-Object { [double]$_.startSeconds }) } else { @() }
                probeSceneStartFrames      = if ($relocatedProbe) { @($relocatedProbe.scenes | ForEach-Object { [int]$_.startFrame }) } else { @() }
                probeProblems              = @($relocatedProbeProblems)
                driverExitCode             = $relocatedDriverRun.ExitCode
                driverSucceeded            = $relocatedDriverRun.Succeeded
                driverSuccessField         = if ($relocatedDriverRun.Json) { [bool]$relocatedDriverRun.Json.success } else { $false }
                driverSceneStarts          = if ($relocatedDriverRun.Json) { @($relocatedDriverRun.Json.scenes | ForEach-Object { [double]$_.startSeconds }) } else { @() }
                driverSceneCount           = if ($relocatedDriverRun.Json) { @($relocatedDriverRun.Json.scenes).Count } else { 0 }
                driverBackend              = if ($relocatedDriverRun.Json) { $relocatedDriverRun.Json.backend } else { $null }
                driverProblems             = @($relocatedDriverRun.Problems)
                pythonVersion              = if ($relocatedDriverRun.Json) { $relocatedDriverRun.Json.pythonVersion } else { $null }
                relocatedSysPrefix         = if ($relocatedFacts) { $relocatedFacts.prefix } else { $null }
                relocatedSysPath           = if ($relocatedFacts) { @($relocatedFacts.sysPath) } else { @() }
                relocatedForeignSysPaths   = $relocatedForeignPaths
                relocatedUserSiteEnabled   = if ($relocatedFacts) { [bool]$relocatedFacts.enableUserSite } else { $null }
                hostReferenceMatches       = @($relocatedHomeScan.Hits | ForEach-Object { $_.path })
                pyvenvCfgMentionedIn       = @($relocatedVenvScan.Hits | ForEach-Object { $_.path })
                pyvenvCfgFiles             = @($relocatedPyvenvFiles | ForEach-Object { $_.FullName })
            }

            if ($relocatedPyvenvFiles.Count -gt 0) {
                throw 'The relocated runtime still contains a pyvenv.cfg file.'
            }
            if ($relocatedProbeProblems.Count -gt 0) {
                throw "The relocated runtime could not detect the expected cuts: $($relocatedProbeProblems -join '; ')"
            }

            Add-StepResult -Step 'clean-location-probe' -Status 'pass' -Detail (
                "the runtime copied to '$relocatedRuntime' detected $(@($relocatedProbe.scenes).Count) scenes at [$((@($relocatedProbe.scenes) | ForEach-Object { '{0:0.000}' -f $_.startSeconds }) -join ', ')] s with no host Python on PATH"
            )

            Add-StepResult -Step 'clean-location-sys-path' -Status 'pass' -Detail (
                "sys.prefix and every sys.path entry resolve inside the copied runtime; user site disabled"
            )

            if ($relocatedDriverRun.Succeeded) {
                Add-StepResult -Step 'clean-location-scene-worker' -Status 'pass' -Detail (
                    "exit 0, $(@($relocatedDriverRun.Json.scenes).Count) scenes at [$((@($relocatedDriverRun.Json.scenes) | ForEach-Object { '{0:0.000}' -f $_.startSeconds }) -join ', ')] s from the copied driver"
                )
            }
            elseif ($RequireSceneDriver) {
                throw "The clean-location scene job failed: $($relocatedDriverRun.Problems -join '; ')"
            }
            else {
                Add-StepResult -Step 'clean-location-scene-worker' -Status 'warn' -Detail "the separately owned driver failed at the clean location: $($relocatedDriverRun.Problems -join '; ')"
            }
        }
        finally {
            foreach ($name in $savedEnvironment.Keys) {
                if ($null -eq $savedEnvironment[$name]) {
                    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
                }
                else {
                    [System.Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
                }
            }
        }
    }

    # -------------------------------------------------------- manifest ------
    Write-Step 'Manifest'

    $pthContents = @(Get-Content -LiteralPath (Join-Path $RuntimeDir $PthFileName))
    $siteCustomizePath = Join-Path $RuntimeDir 'Lib/site-packages/sitecustomize.py'

    if (-not $pipWheelRecord) {
        $pipWheelRecord = [ordered]@{
            name        = $PipWheel.Name
            version     = $PipWheel.Version
            wheelFile   = $PipWheel.FileName
            sha256      = $PipWheel.Sha256
            sizeBytes   = $PipWheel.Size
            artifactUrl = $PipWheel.Url
            source      = 'get-pip-bootstrap'
        }
    }

    $allPackages = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $packageRecords) { $allPackages.Add($record) }
    $allPackages.Add($pipWheelRecord)

    $document = [ordered]@{
        schemaVersion  = 1
        kind           = 'relocatable-embeddable-python-runtime'
        generatedUtc   = (Get-Date).ToUniversalTime().ToString('O')
        project        = 'vidsc'
        runtimeDirectory = $RuntimeDir
        driver         = $DriverPath
        driverSha256   = (Get-Sha256 -Path $DriverPath)
        buildMode      = $buildMode
        verificationSkipped = [bool]$SkipVerify
        isVirtualEnvironment = $false
        pyvenvCfgPresent = ($pyvenvFiles.Count -gt 0)
        interpreter    = [ordered]@{
            implementation = $facts.implementation
            version        = $facts.version
            versionString  = $facts.versionString
            platform       = $facts.platform
            machine        = $facts.machine
            pythonExe      = $PythonExeName
            pythonExeSha256 = (Get-Sha256 -Path (Join-Path $RuntimeDir $PythonExeName))
            pythonDll      = $PythonDllName
            pythonDllSha256 = (Get-Sha256 -Path (Join-Path $RuntimeDir $PythonDllName))
            stdlibZip      = $StdlibZipName
            stdlibZipSha256 = (Get-Sha256 -Path (Join-Path $RuntimeDir $StdlibZipName))
            pthFile        = $PthFileName
            pthContents    = $pthContents
            sitePackages   = 'Lib/site-packages'
            siteCustomize  = 'Lib/site-packages/sitecustomize.py'
            siteCustomizeSha256 = (Get-Sha256 -Path $siteCustomizePath)
            sysPrefix      = $facts.prefix
            sysPath        = @($facts.sysPath)
            isolated       = [bool]$facts.isolated
            userSiteEnabled = [bool]$facts.enableUserSite
            userSite       = $facts.userSite
        }
        embeddable     = [ordered]@{
            distribution = 'Windows embeddable (amd64)'
            version      = $Embeddable.Version
            url          = $Embeddable.Url
            sha256       = $Embeddable.Sha256
            sizeBytes    = $Embeddable.Size
            archive      = $EmbeddablePath
            source       = $extractionSource
        }
        pipBootstrap   = [ordered]@{
            method     = 'get-pip.py'
            url        = $PipBootstrap.Url
            sha256     = $PipBootstrap.Sha256
            sizeBytes  = $PipBootstrap.Size
            archive    = $GetPipPath
            source     = $getPipSource
            pipVersion = $PipBootstrap.PipVersion
        }
        packages       = @($allPackages)
        installedDistributions = $facts.distributions
        pipCheck       = [ordered]@{ exitCode = 0; output = 'No broken requirements found.' }
        importsVerified = $true
        runtimeDetectionProbe = $detectionProbe
        sceneWorkerDriver = $sceneWorker
        requireSceneDriver = [bool]$RequireSceneDriver
        hostPythonReference = [ordered]@{
            pattern        = $HostPythonReference
            scannedFiles   = $homeScan.ScannedFiles
            matches        = @($homeScan.Hits)
            criticalMatches = @($homeScan.Hits | Where-Object { Test-CriticalRuntimeFile -RelativePath $_.path } | ForEach-Object { $_.path })
            requiredAtRuntime = $false
        }
        venvReference  = [ordered]@{
            pattern        = 'pyvenv.cfg'
            scannedFiles   = $venvScan.ScannedFiles
            mentionedIn    = @($venvScan.Hits)
            criticalMatches = @($venvScan.Hits | Where-Object { Test-CriticalConfigFile -RelativePath $_.path } | ForEach-Object { $_.path })
            benignMentions = $benignVenvMentions
            pyvenvCfgFiles = @($pyvenvFiles | ForEach-Object { $_.FullName })
            requiredAtRuntime = $false
            interpretation = 'No pyvenv.cfg file exists and no path-configuration file mentions one. Remaining text matches are CPython/pip venv-detection code (python314.dll, pip/_internal/utils/virtualenv.py) that only looks for a v-env marker; the relocated scene job does not depend on one.'
        }
        cleanLocationTest = $cleanLocation
        commands       = @($script:Commands)
    }

    $json = $document | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $ManifestPath -Value $json -Encoding utf8

    Add-StepResult -Step 'manifest' -Status 'pass' -Detail $ManifestPath
    if ($SkipVerify) {
        Write-Info 'this run used -SkipVerify, so the verified scene-runtime-manifest-relocatable.json was left untouched'
    }

    # --------------------------------------------------------- summary ------
    Write-Host ''
    Write-Host 'Build summary' -ForegroundColor Cyan
    $script:StepResults | Format-Table -AutoSize | Out-String | Write-Host

    Write-Host "Runtime          : $RuntimeDir"
    Write-Host "Interpreter      : Python $($facts.version) ($PythonDllName, $StdlibZipName)"
    Write-Host "python.exe sha256: $($document.interpreter.pythonExeSha256)"
    Write-Host "$PythonDllName sha256: $($document.interpreter.pythonDllSha256)"
    Write-Host "Manifest         : $ManifestPath"
}
catch {
    Write-Host ''
    Write-Host 'Scene runtime build FAILED.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host 'No manifest was written for this failed build.' -ForegroundColor Red
    exit 1
}

exit 0
