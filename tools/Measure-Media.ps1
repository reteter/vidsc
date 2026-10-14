[CmdletBinding()]
param(
    [ValidateSet('open','seek','playback','lifecycle')] [string]$Scenario = 'open',
    [string]$Manifest,
    [string[]]$Media = @(),
    [ValidateRange(1,2147483647)] [int]$DurationSeconds = 900,
    [string]$ReleaseRoot,
    [string]$StorageNotes,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$ReleaseRoot) { $ReleaseRoot = Join-Path $projectRoot 'artifacts/release' }
if (!$OutputRoot) { $OutputRoot = Join-Path $projectRoot 'artifacts/benchmarks' }
$exe = Join-Path $ReleaseRoot 'app/Vidsc.App.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Release application missing: $exe" }
if ($Manifest) {
    $inputManifest = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $Media += @($inputManifest.media | ForEach-Object { $_.path })
}
if (!$Media.Count) { throw 'Supply -Media or a local -Manifest with media:[{name,path,storageNotes}]. No production media defaults exist.' }
$Media = @($Media | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
$runRoot = Join-Path $OutputRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$identity = [ordered]@{ generatedUtc=[datetime]::UtcNow; releaseRoot=$ReleaseRoot; scenario=$Scenario; durationSeconds=$DurationSeconds; seed=10414; protocol='docs/validation/2026-10-14-m1-benchmark-protocol.md'; storageNotes=$StorageNotes; osCacheState='unobserved; no cache clearing'; inputs=@($Media | ForEach-Object { $f=Get-Item -LiteralPath $_; @{path=$f.FullName;length=$f.Length;lastWriteUtc=$f.LastWriteTimeUtc} }) }
try {
    $identity.cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    $identity.system = Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory,Model
    $identity.os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
    $identity.gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,PNPDeviceID)
    $identity.storage = @(Get-CimInstance Win32_DiskDrive | Select-Object Model,MediaType,InterfaceType,Size)
} catch { $identity.cimIdentityUnavailable = $_.Exception.Message }
if ($Manifest) { $identity.inputManifest = $inputManifest }
$identity.tools = @(Get-ChildItem -LiteralPath (Join-Path $ReleaseRoot 'app/tools/ffmpeg') -Filter '*.exe' | Select-Object Name,@{n='version';e={$_.VersionInfo.FileVersion}},Length)
$identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'manifest.json')

function Invoke-OwnedRun([string[]]$Paths, [string]$Name, [int]$OpenCount) {
    $report = Join-Path $runRoot "$Name.json"
    $startInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['VIDSC_WORK_DIR'] = Join-Path $runRoot "work-$Name"
    foreach ($value in @('--benchmark',$Scenario,'--report',$report,'--duration-seconds',"$DurationSeconds",'--open-count',"$OpenCount")) { $startInfo.ArgumentList.Add($value) }
    foreach ($path in $Paths) { $startInfo.ArgumentList.Add('--media'); $startInfo.ArgumentList.Add($path) }
    $ownedProcess = [Diagnostics.Process]::Start($startInfo)
    $samples = [Collections.Generic.List[object]]::new()
    $timeout = switch ($Scenario) { 'playback' { ($DurationSeconds + 180) * $Paths.Count }; 'lifecycle' { 35 * 135 + 180 }; 'seek' { 3200 * $Paths.Count + 180 }; default { 120 * $OpenCount * $Paths.Count + 180 } }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (!$ownedProcess.HasExited) {
            if ($watch.Elapsed.TotalSeconds -gt $timeout) { throw "Owned benchmark timed out after $timeout seconds." }
            try {
                # Snapshot ancestry at each sample; never select unrelated processes by executable name.
                $snapshot = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
                $ids = [Collections.Generic.HashSet[int]]::new(); [void]$ids.Add($ownedProcess.Id)
                do { $added=$false; foreach ($item in $snapshot) { if ($ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)) { $added=$true } } } while ($added)
                $members = @(); foreach ($processId in $ids) { try { $p=Get-Process -Id $processId -ErrorAction Stop; $members += @{pid=$p.Id;privateBytes=$p.PrivateMemorySize64;workingSetBytes=$p.WorkingSet64;handles=$p.HandleCount;cpuSeconds=$p.TotalProcessorTime.TotalSeconds} } catch { } }
                $samples.Add(@{seconds=$watch.Elapsed.TotalSeconds;members=$members;privateBytes=($members.privateBytes | Measure-Object -Sum).Sum;workingSetBytes=($members.workingSetBytes | Measure-Object -Sum).Sum;handles=($members.handles | Measure-Object -Sum).Sum;childCount=[Math]::Max(0,$members.Count-1)})
            } catch { $samples.Add(@{seconds=$watch.Elapsed.TotalSeconds;treeUnavailable=$_.Exception.Message}) }
            Start-Sleep -Milliseconds 1000
            $ownedProcess.Refresh()
        }
        if ($ownedProcess.ExitCode -ne 0) { throw "Benchmark exited $($ownedProcess.ExitCode). Report: $report" }
    } finally {
        $cleanupError = $null
        try {
            if (!$ownedProcess.HasExited) {
                $ownedProcess.Kill($true)
                if (!$ownedProcess.WaitForExit(5000)) { $cleanupError = 'Owned benchmark remains running five seconds after termination request.' }
            }
        } catch { $cleanupError = 'Owned benchmark cleanup failed: ' + $_.Exception.Message }
        try {
            if ($cleanupError) { $samples.Add(@{cleanupFailure=$cleanupError}) }
            $samples | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot "$Name-process-tree.json")
        } finally { $ownedProcess.Dispose() }
        if ($cleanupError) { throw $cleanupError }
    }
}
if ($Scenario -eq 'open') {
    for ($i=0; $i -lt $Media.Count; $i++) {
        for ($j=1; $j -le 3; $j++) { Invoke-OwnedRun @($Media[$i]) "media-$i-fresh-$j" 1 }
        Invoke-OwnedRun @($Media[$i]) "media-$i-warm" 4 # First open primes this process; next three are warm.
    }
} else { Invoke-OwnedRun $Media $Scenario 3 }
Write-Output $runRoot


