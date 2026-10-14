#Requires -Version 7.0
<#
.SYNOPSIS
    Exercises tools/Promotion.ps1 in isolated, script-owned directories.

.DESCRIPTION
    Runs four scenarios fully under artifacts/logs/promotion-test (never the real release tree, never
    the user's running application):
      A. a failed verification preserves the existing release;
      B. a normal promotion moves the previous release to trash and installs the staged release;
      C. an injected second-move failure restores the previous release (rollback);
      D. an injected second-move failure that also fails to roll back is reported explicitly.

    Exits 0 when every scenario behaves as required.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)
    if ($Condition) {
        Write-Host "  ok   $Message"
    }
    else {
        Write-Host "  FAIL $Message" -ForegroundColor Red
        $script:failures.Add($Message)
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$promotionPath = Join-Path $PSScriptRoot 'Promotion.ps1'

. $promotionPath

$runRoot = Join-Path $artifactsRoot ('logs/promotion-test/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function New-Scenario {
    param([Parameter(Mandatory)][string] $Name)

    $root = Join-Path $runRoot $Name
    $release = Join-Path $root 'release'
    $stage = Join-Path $root 'staging/release'
    $trash = Join-Path $root 'trash'

    New-Item -ItemType Directory -Force -Path $release, $stage, $trash | Out-Null
    Set-Content -LiteralPath (Join-Path $release 'v.txt') -Value 'old' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $stage 'v.txt') -Value 'new' -Encoding utf8

    return [pscustomobject]@{ Root = $root; Release = $release; Stage = $stage; Trash = $trash }
}

Write-Host 'Scenario A: verification failure preserves the existing release'
$s = New-Scenario -Name 'A'
$result = Invoke-ReleasePromotion -ReleaseRoot $s.Release -StageRelease $s.Stage -TrashRoot $s.Trash -VerifyFailures @('injected verification failure')
Assert-True (-not $result.Promoted) 'promotion refused'
Assert-True ((Get-Content -LiteralPath (Join-Path $s.Release 'v.txt') -Raw).Trim() -eq 'old') 'existing release is unchanged'
Assert-True (Test-Path -LiteralPath (Join-Path $s.Stage 'v.txt')) 'staged release is untouched'

Write-Host 'Scenario B: normal promotion promotes and keeps the previous release in trash'
$s = New-Scenario -Name 'B'
$result = Invoke-ReleasePromotion -ReleaseRoot $s.Release -StageRelease $s.Stage -TrashRoot $s.Trash
Assert-True ($result.Promoted) 'promotion succeeded'
Assert-True ((Get-Content -LiteralPath (Join-Path $s.Release 'v.txt') -Raw).Trim() -eq 'new') 'release now holds the staged build'
$trashSnapshots = @(Get-ChildItem -LiteralPath $s.Trash -Directory)
Assert-True ($trashSnapshots.Count -eq 1) 'exactly one previous-release snapshot exists'
Assert-True ($trashSnapshots.Count -eq 1 -and (Get-Content -LiteralPath (Join-Path $trashSnapshots[0].FullName 'release/v.txt') -Raw).Trim() -eq 'old') 'snapshot holds the previous release'

Write-Host 'Scenario C: injected second-move failure rolls back to the previous release'
$s = New-Scenario -Name 'C'
$script:promotionMoveCount = 0
$failingSecondMove = {
    param($From, $To)
    $script:promotionMoveCount++
    if ($script:promotionMoveCount -eq 2) { throw 'injected second-move failure' }
    Move-Item -LiteralPath $From -Destination $To -Force
}
$result = Invoke-ReleasePromotion -ReleaseRoot $s.Release -StageRelease $s.Stage -TrashRoot $s.Trash -MoveItem $failingSecondMove
Assert-True (-not $result.Promoted) 'promotion failed'
Assert-True ($result.RolledBack) 'rollback was performed'
Assert-True (-not $result.RollbackFailed) 'rollback did not itself fail'
Assert-True ((Get-Content -LiteralPath (Join-Path $s.Release 'v.txt') -Raw).Trim() -eq 'old') 'previous release was restored'

Write-Host 'Scenario D: a second-move failure whose rollback also fails is reported explicitly'
$s = New-Scenario -Name 'D'
$script:promotionMoveCount = 0
$failingMoveAndRollback = {
    param($From, $To)
    $script:promotionMoveCount++
    if ($script:promotionMoveCount -ge 2) { throw 'injected move failure' }
    Move-Item -LiteralPath $From -Destination $To -Force
}
$result = Invoke-ReleasePromotion -ReleaseRoot $s.Release -StageRelease $s.Stage -TrashRoot $s.Trash -MoveItem $failingMoveAndRollback
Assert-True (-not $result.Promoted) 'promotion failed'
Assert-True ($result.RollbackFailed) 'rollback failure was reported'
Assert-True (Test-Path -LiteralPath $result.BackupPath) 'the previous release is still available at the reported backup path'

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host 'Promotion rollback tests: ALL PASSED' -ForegroundColor Green
    exit 0
}

Write-Host ("Promotion rollback tests: {0} FAILURE(S)" -f $script:failures.Count) -ForegroundColor Red
foreach ($failure in $script:failures) { Write-Host "  - $failure" -ForegroundColor Red }
exit 1
