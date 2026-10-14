[CmdletBinding()]
param(
    [ValidateSet('open','seek','playback','lifecycle','contention')] [string]$Scenario = 'open',
    [string]$Manifest,
    [string[]]$Media = @(),
    [ValidateRange(1,2147483647)] [int]$DurationSeconds = 900,
    [string]$ReleaseRoot,
    [string]$StorageNotes,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
if ($Scenario -eq 'contention' -and !$PSBoundParameters.ContainsKey('DurationSeconds')) { $DurationSeconds = 300 }
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
$identity = [ordered]@{ generatedUtc=[datetime]::UtcNow; releaseRoot=$ReleaseRoot; scenario=$Scenario; durationSeconds=$DurationSeconds; durationClassification=$(if ($DurationSeconds -eq $(if ($Scenario -eq 'contention') {300} else {900})) {'protocol-duration'} else {'harness-smoke-only'}); seed=10414; protocol='docs/validation/2026-10-14-m1-benchmark-protocol.md'; storageNotes=$StorageNotes; osCacheState='unobserved; no cache clearing'; inputs=@($Media | ForEach-Object { $f=Get-Item -LiteralPath $_; @{path=$f.FullName;length=$f.Length;lastWriteUtc=$f.LastWriteTimeUtc} }) }
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
    # PID alone can be reused. Retain each observed identity even when its parent exits.
    $seen = @{}
    $rootIdentity = @{ pid=$ownedProcess.Id; startedUtc=$ownedProcess.StartTime.ToUniversalTime().ToString('O') }
    $seen["$($rootIdentity.pid)|$($rootIdentity.startedUtc)"] = $rootIdentity
    $timeout = switch ($Scenario) { 'playback' { ($DurationSeconds + 180) * $Paths.Count }; 'contention' { 3 * (3200 + $DurationSeconds + 180) * $Paths.Count }; 'lifecycle' { 35 * 135 + 180 }; 'seek' { 3200 * $Paths.Count + 180 }; default { 120 * $OpenCount * $Paths.Count + 180 } }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (!$ownedProcess.HasExited) {
            if ($watch.Elapsed.TotalSeconds -gt $timeout) { throw "Owned benchmark timed out after $timeout seconds." }
            try {
                $snapshot = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CreationDate)
                $current = @{}
                foreach ($item in $snapshot) { $current[[int]$item.ProcessId] = $item }
                $ids = [Collections.Generic.HashSet[int]]::new()
                foreach ($entry in $seen.Values) {
                    $item = $current[[int]$entry.pid]
                    if ($item -and $item.CreationDate.ToUniversalTime().ToString('O') -eq $entry.startedUtc) { [void]$ids.Add([int]$entry.pid) }
                }
                do {
                    $added=$false
                    foreach ($item in $snapshot) {
                        $parent = $current[[int]$item.ParentProcessId]
                        if ($parent -and $ids.Contains([int]$item.ParentProcessId) -and $item.CreationDate -ge $parent.CreationDate -and $ids.Add([int]$item.ProcessId)) { $added=$true }
                    }
                } while ($added)
                $members = @()
                foreach ($processId in $ids) {
                    try {
                        $p=Get-Process -Id $processId -ErrorAction Stop
                        $started=$p.StartTime.ToUniversalTime().ToString('O')
                        if ($started -ne $current[$processId].CreationDate.ToUniversalTime().ToString('O')) { continue }
                        $entry=@{pid=$p.Id;startedUtc=$started;privateBytes=$p.PrivateMemorySize64;workingSetBytes=$p.WorkingSet64;handles=$p.HandleCount;cpuSeconds=$p.TotalProcessorTime.TotalSeconds}
                        $seen["$($p.Id)|$started"] = @{pid=$p.Id;startedUtc=$started}
                        $members += $entry
                        $p.Dispose()
                    } catch { }
                }
                $gpu = @(); $gpuUnavailable=$null
                try {
                    $counters = Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
                    $gpu = @($counters.CounterSamples | Where-Object {
                        $_.InstanceName -match '^pid_(\d+)_luid_' -and $ids.Contains([int]$Matches[1])
                    } | Select-Object InstanceName,CookedValue,Timestamp)
                } catch { $gpuUnavailable=$_.Exception.Message }
                $samples.Add(@{seconds=$watch.Elapsed.TotalSeconds;members=$members;gpuEngines=$gpu;gpuUnavailable=$gpuUnavailable;privateBytes=($members.privateBytes | Measure-Object -Sum).Sum;workingSetBytes=($members.workingSetBytes | Measure-Object -Sum).Sum;handles=($members.handles | Measure-Object -Sum).Sum;childCount=[Math]::Max(0,$members.Count-1)})
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
            $survivors = @(); $verificationUnavailable = $null
            try {
                foreach ($entry in $seen.Values) {
                    try {
                        $remaining = Get-Process -Id $entry.pid -ErrorAction Stop
                        if ($remaining.StartTime.ToUniversalTime().ToString('O') -eq $entry.startedUtc) { $survivors += $entry }
                        $remaining.Dispose()
                    } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
                }
            } catch { $verificationUnavailable=$_.Exception.Message }
            $samples.Add(@{kind='post-parent-exit';seenIdentities=@($seen.Values);survivors=$survivors;status=$(if ($verificationUnavailable) {'unavailable'} elseif ($survivors.Count) {'failed'} else {'passed'});verificationUnavailable=$verificationUnavailable})
            if ($survivors.Count) { $cleanupError='Previously observed owned process identities remain after parent exit.' }
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
