#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the standalone repository's compile, unit and promotion checks without native playback.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $projectRoot ('artifacts/ci/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$exitCode = 0

function Invoke-Check {
    param([string]$Name, [string]$Executable, [string[]]$Arguments)
    Write-Host "Running $Name"
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runRoot "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "$Name exited with code $LASTEXITCODE." }
}

Push-Location $projectRoot
Start-Transcript -LiteralPath (Join-Path $runRoot 'transcript.log') | Out-Null
try {
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
    $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
    Invoke-Check -Name 'sdk' -Executable $dotnet -Arguments @('--info')

    # Git limits parsing to repository-owned scripts, excluding generated artifacts and runtime downloads.
    $scripts = @(& $git ls-files -- '*.ps1' '*.psm1' '*.psd1')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked PowerShell scripts.' }
    $parseFailures = [Collections.Generic.List[string]]::new()
    foreach ($script in $scripts) {
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot $script), [ref]$null, [ref]$parseErrors) | Out-Null
        foreach ($parseError in $parseErrors) { $parseFailures.Add("${script}: $parseError") }
    }
    @("Parsed $($scripts.Count) tracked PowerShell scripts.") + @($parseFailures) | Set-Content -LiteralPath (Join-Path $runRoot 'powershell-parser.log')
    if ($parseFailures.Count) { throw ($parseFailures -join [Environment]::NewLine) }

    Invoke-Check -Name 'promotion' -Executable $pwsh -Arguments @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-Promotion.ps1'))
    Invoke-Check -Name 'release-package' -Executable $pwsh -Arguments @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1'))
    Invoke-Check -Name 'restore' -Executable $dotnet -Arguments @('restore', 'Vidsc.slnx', '--verbosity', 'minimal')
    Invoke-Check -Name 'build' -Executable $dotnet -Arguments @('build', 'Vidsc.slnx', '--configuration', 'Release', '--no-restore', '-m:1', '--verbosity', 'minimal', "-bl:$(Join-Path $runRoot 'build.binlog')")
    Invoke-Check -Name 'test' -Executable $dotnet -Arguments @('test', 'Vidsc.slnx', '--configuration', 'Release', '--no-build', '--no-restore', '-m:1', '--verbosity', 'minimal', '--logger', 'trx', '--results-directory', (Join-Path $runRoot 'tests'))
}
catch {
    $exitCode = 1
    $_ | Out-String | Tee-Object -FilePath (Join-Path $runRoot 'failure.log') | Write-Host
}
finally {
    Write-Host "CI logs: $runRoot"
    Stop-Transcript | Out-Null
    Pop-Location
}
exit $exitCode
