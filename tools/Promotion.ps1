#Requires -Version 7.0
<#
.SYNOPSIS
    Promotes a verified staged release into place with a rollback on failure.

.DESCRIPTION
    Extracted from tools/Build-Release.ps1 so the promotion transaction can be exercised in isolation
    by tools/Test-Promotion.ps1 without a full publish.

    Rules:
      * A non-empty VerifyFailures list refuses the promotion outright; the existing release is left
        untouched.
      * The previous release is moved aside first, then the staged release is moved into place.
      * If the second move fails, the previous release is moved back (rollback). A failing rollback is
        reported explicitly instead of being hidden.

.PARAMETER MoveItem
    Injection seam for tests: a scriptblock that moves From -> To. Defaults to Move-Item -Force.
#>
function Invoke-ReleasePromotion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ReleaseRoot,
        [Parameter(Mandatory)][string] $StageRelease,
        [Parameter(Mandatory)][string] $TrashRoot,
        [string[]] $VerifyFailures = @(),
        [scriptblock] $MoveItem = { param($From, $To) Move-Item -LiteralPath $From -Destination $To -Force }
    )

    if ($VerifyFailures.Count -gt 0) {
        return [pscustomobject]@{
            Promoted       = $false
            Reason         = 'verification-failed'
            Error          = $null
            RolledBack     = $false
            RollbackFailed = $false
            BackupPath     = $null
        }
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $trashDir = Join-Path $TrashRoot $stamp
    $backupPath = Join-Path $trashDir 'release'
    $hadPrevious = Test-Path -LiteralPath $ReleaseRoot

    if ($hadPrevious) {
        New-Item -ItemType Directory -Force -Path $trashDir | Out-Null

        try {
            & $MoveItem $ReleaseRoot $backupPath
        }
        catch {
            return [pscustomobject]@{
                Promoted       = $false
                Reason         = 'backup-move-failed'
                Error          = $_.Exception.Message
                RolledBack     = $false
                RollbackFailed = $false
                BackupPath     = $backupPath
            }
        }
    }

    try {
        & $MoveItem $StageRelease $ReleaseRoot
    }
    catch {
        $moveError = $_.Exception.Message
        $rolledBack = $false
        $rollbackFailed = $false

        if ($hadPrevious) {
            try {
                & $MoveItem $backupPath $ReleaseRoot
                $rolledBack = $true
            }
            catch {
                $rollbackFailed = $true
            }
        }

        return [pscustomobject]@{
            Promoted       = $false
            Reason         = 'promotion-move-failed'
            Error          = $moveError
            RolledBack     = $rolledBack
            RollbackFailed = $rollbackFailed
            BackupPath     = $backupPath
        }
    }

    return [pscustomobject]@{
        Promoted       = $true
        Reason         = 'promoted'
        Error          = $null
        RolledBack     = $false
        RollbackFailed = $false
        BackupPath     = $backupPath
    }
}
