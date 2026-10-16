# Manual Windows B6 check. Requires an existing runtime bundle, an installed .NET Desktop
# Runtime and a media fixture; launches native tools and the application. No build is performed.
[CmdletBinding()]
param(
    [string]$ReleaseRoot = "$PSScriptRoot/../artifacts/release",
    [string]$Fixture = "$PSScriptRoot/../artifacts/tests/fixtures/scene-smoke.mp4",
    [string]$RunId = ([Guid]::NewGuid().ToString('N')),
    [switch]$NonGui
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Release-Package.ps1"
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../artifacts/delivery/$RunId clean Ünïcode")
if (Test-Path -LiteralPath $root) { throw "Output already exists: $root" }
$null = New-Item -ItemType Directory -Path $root
$failures = [Collections.Generic.List[string]]::new()
$runs = [Collections.Generic.List[object]]::new()

function Invoke-Owned([string]$Executable, [string[]]$Arguments, [string]$Name, [int]$TimeoutSeconds = 90) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $root
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['PATH'] = "$env:WINDIR/System32;$env:WINDIR"
    foreach ($key in @('VIDSC_NATIVE_DIR','VIDSC_FFMPEG_DIR','VIDSC_SCENE_DIR','PYTHONHOME','PYTHONPATH')) { $null = $start.Environment.Remove($key) }
    $start.Environment['VIDSC_WORK_DIR'] = Join-Path $root 'fresh work state'
    $start.Environment['TEMP'] = Join-Path $root 'fresh temp'
    $start.Environment['TMP'] = $start.Environment['TEMP']
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $root 'fresh extraction cache'
    $null = New-Item -ItemType Directory -Path $start.Environment['TEMP'] -Force
    $process = $null
    $failure = $null
    $cleanupFailure = $null
    $exitCode = $null
    try {
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) { throw "$Name timed out." }
        # A descendant can retain a redirected pipe after the immediate child exits.
        $outText = $stdout.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        $errText = $stderr.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        $outText | Set-Content -LiteralPath (Join-Path $root "$Name.stdout.txt")
        $errText | Set-Content -LiteralPath (Join-Path $root "$Name.stderr.txt")
        $exitCode = $process.ExitCode
        $runs.Add(@{ name=$Name; executable=$Executable; arguments=$Arguments; exitCode=$exitCode })
    } catch { $failure = $_.Exception }
    finally {
        if ($null -ne $process) {
            try {
                if (!$process.HasExited) {
                    $process.Kill($true)
                    if (!$process.WaitForExit(5000)) { throw "$Name owned process did not exit after termination." }
                }
            } catch { $cleanupFailure = $_.Exception }
            finally {
                try { $process.Dispose() }
                catch {
                    if ($null -eq $cleanupFailure) { $cleanupFailure = $_.Exception }
                    else { $cleanupFailure = [AggregateException]::new('Termination and disposal failed.', [Exception[]]@($cleanupFailure, $_.Exception)) }
                }
            }
        }
    }
    if ($null -ne $failure -and $null -ne $cleanupFailure) {
        throw [AggregateException]::new("$Name failed and owned process cleanup failed.", [Exception[]]@($failure, $cleanupFailure))
    }
    if ($null -ne $failure) { throw $failure }
    if ($null -ne $cleanupFailure) { throw $cleanupFailure }
    return $exitCode
}

function Read-RequiredReport([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "Required report is missing or empty: $Path"
    }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $report) { throw "Required report has no JSON result: $Path" }
    return $report
}

try {
    $release = (Resolve-Path -LiteralPath $ReleaseRoot).Path
    $null = Test-ReleaseInventory -Root (Join-Path $release 'app')
    Copy-Item -LiteralPath (Join-Path $release 'app') -Destination (Join-Path $root 'app') -Recurse
    $null = Test-ReleaseInventory -Root (Join-Path $root 'app')
    $media = Join-Path $root 'source media with spaces.mp4'
    if ($NonGui) {
        # Build the adapter driver separately; never add test assemblies to the measured package.
        $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
        $runnerRoot = Join-Path $root 'adapter driver'
        New-Item -ItemType Directory -Path $runnerRoot | Out-Null
        $references = Get-ChildItem -LiteralPath (Join-Path $root 'app') -Filter '*.dll' -File | ForEach-Object {
            $name = [Security.SecurityElement]::Escape($_.BaseName)
            $path = [Security.SecurityElement]::Escape($_.FullName)
            "<Reference Include=`"$name`"><HintPath>$path</HintPath></Reference>"
        }
        $source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'verification/M8Delivery.cs'))
        @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PlatformTarget>x64</PlatformTarget><UseAppHost>false</UseAppHost><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup>$references<Compile Include="$source"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $runnerRoot 'M8Delivery.csproj') -Encoding utf8
        & $dotnet build (Join-Path $runnerRoot 'M8Delivery.csproj') -c Release -o (Join-Path $runnerRoot 'bin') *> (Join-Path $root 'adapter-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Delivery adapter driver compilation failed.' }
        # Resolution from the driver directory is explicit; package bytes remain untouched.
        Get-ChildItem -LiteralPath (Join-Path $root 'app') -Filter '*.dll' -File | Copy-Item -Destination (Join-Path $runnerRoot 'bin')
        $ffmpeg = Join-Path $root 'app/tools/ffmpeg/ffmpeg.exe'
        if ((Invoke-Owned $ffmpeg @('-hide_banner','-nostdin','-v','error','-n','-f','lavfi','-i','testsrc2=size=320x180:rate=24:duration=6','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=6','-c:v','libx264','-pix_fmt','yuv420p','-g','24','-keyint_min','24','-sc_threshold','0','-bf','2','-c:a','aac','-ar','48000','-ac','6','-shortest',$media) 'fixture') -ne 0) { throw 'Delivery fixture generation failed.' }
        $before = (Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash
        $adapterOutput = Join-Path $root 'adapter results'
        if ((Invoke-Owned $dotnet @((Join-Path $runnerRoot 'bin/M8Delivery.dll'),(Join-Path $root 'app'),$media,$adapterOutput) 'adapters' 300) -ne 0) { throw 'Packaged adapter delivery failed.' }
        $summary = Read-RequiredReport (Join-Path $adapterOutput 'summary.json')
        if (!$summary.passed) { throw 'Packaged adapter summary did not pass.' }
        if ((Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash -ne $before) { throw 'Source bytes changed.' }
    }
    else {
    $fixturePath = (Resolve-Path -LiteralPath $Fixture).Path
    Copy-Item -LiteralPath $fixturePath -Destination $media
    $before = (Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash
    $app = Join-Path $root 'app'
    $cli = Join-Path $app 'tools/ffmpeg'
    $copy = Join-Path $root 'copy result.mp4'
    if ((Invoke-Owned (Join-Path $cli 'ffprobe.exe') @('-v','error','-show_streams','-of','json',$media) 'probe') -ne 0) { throw 'Packaged probe failed.' }
    $copyArguments = @('-v','error','-n','-i',$media,'-t','1','-map','0:v:0','-map','0:a:0?','-c','copy',$copy)
    if ((Invoke-Owned (Join-Path $cli 'ffmpeg.exe') $copyArguments 'copy') -ne 0) { throw 'Packaged copy failed.' }
    $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
    if ((Invoke-Owned (Join-Path $cli 'ffmpeg.exe') $copyArguments 'no-clobber') -eq 0) { throw 'Existing output was not rejected.' }
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $copyHash) { throw 'Existing copy bytes changed.' }
    $sceneReport = Join-Path $root 'scene.json'
    if ((Invoke-Owned (Join-Path $app 'scene/python/python.exe') @((Join-Path $app 'scene/vidsc_scene_job.py'),'--input',$media,'--output',$sceneReport,'--detector','content','--start','0','--duration','3') 'scene') -ne 0) { throw 'Packaged scene process failed.' }
    $scene = Read-RequiredReport $sceneReport
    if (!$scene.success -or $scene.backend -ne 'pyav') { throw 'Packaged scene result failed.' }
    $smokeReport = Join-Path $root 'app-smoke.json'
    if ((Invoke-Owned (Join-Path $app 'Vidsc.App.exe') @('--smoke-test','--media',$media,'--report',$smokeReport,'--log',(Join-Path $root 'app.log')) 'application' 120) -ne 0) { throw 'Application workflow or graceful shutdown failed.' }
    $smoke = Read-RequiredReport $smokeReport
    if (!$smoke.passed -or !$smoke.shutdownJobStarted) { throw 'Application smoke or active-job shutdown evidence missing.' }
    foreach ($path in @($smoke.engine.nativeDirectory,$smoke.tools.ffmpegPath,$smoke.tools.ffprobePath,$smoke.tools.scenePythonPath)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($app + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Tool escaped clean bundle: $path" }
    }
    if (@(Get-ChildItem -LiteralPath $app -Recurse -Filter 'coreclr.dll').Count) { throw '.NET runtime unexpectedly bundled.' }
    if ((Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash -ne $before) { throw 'Source bytes changed.' }
    }
    $null = Test-ReleaseInventory -Root (Join-Path $root 'app')
    if (Test-Path -LiteralPath (Join-Path $root 'fresh extraction cache')) {
        if (@(Get-ChildItem -LiteralPath (Join-Path $root 'fresh extraction cache') -Force).Count) { throw 'Expanded package unexpectedly used a bundle extraction cache.' }
    }
} catch { $failures.Add($_.Exception.ToString()) }
@{ schemaVersion=1; generatedUtc=[DateTime]::UtcNow; root=$root; passed=$failures.Count -eq 0;
    scope= $(if ($NonGui) { 'M8 non-GUI production adapters: scenes/project save-reopen/expanded-copy/accurate/no-clobber' } else { 'M1 visible prototype delivery workflow' });
    guiValidated=(!$NonGui); pathPolicy='Windows-only PATH; bundle overrides removed; fresh work/temp';
    runs=@($runs); failures=@($failures) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'delivery.json')
Write-Output $root
if ($failures.Count) { exit 1 }
