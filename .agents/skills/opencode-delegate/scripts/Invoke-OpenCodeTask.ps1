#Requires -Version 7.0
<#
.SYNOPSIS
Delegates a task through the repository's OpenCode V2 runner.
.DESCRIPTION
Accepts inline text or a UTF-8 prompt file. Auto-approval is enabled by default
for this project's delegation workflow; use -Auto:$false to disable it.
Effort selects an OpenCode catalog variant, not a separate provider setting.
The script does not change sandbox permissions or verify session metadata.
.PARAMETER Model
Full provider/model identifier, optionally with #variant. The local DeepSeek
short name is also accepted. Other short names must be resolved by the caller.
.PARAMETER Effort
Variant ID appended to the model. Variant is an alias. Conflicting suffixes fail.
.PARAMETER OutputPath
JSONL log path. If omitted, a unique ignored artifact directory is created.
.EXAMPLE
pwsh -NoProfile -File ./Invoke-OpenCodeTask.ps1 -PromptFile ./task.txt -WorkingDirectory D:\code\my-app -Model deepseek-v4.1-flash -Effort high
.EXAMPLE
./Invoke-OpenCodeTask.ps1 -Prompt 'Explain the code' -WorkingDirectory D:\code\my-app -Auto:$false
#>
[CmdletBinding(DefaultParameterSetName = 'Text')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Text')]
    [ValidateNotNullOrEmpty()]
    [string] $Prompt,

    [Parameter(Mandatory, ParameterSetName = 'File')]
    [ValidateNotNullOrEmpty()]
    [string] $PromptFile,

    [ValidateNotNullOrEmpty()]
    [string] $Model = 'opencode-go/deepseek-v4.1-flash',

    [Alias('Variant')]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $Effort,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $WorkingDirectory,

    [ValidateRange(0, 86400)]
    [int] $TimeoutSeconds = 600,

    [switch] $Auto = $true,

    [string] $OutputPath,

    [ValidateNotNullOrEmpty()]
    [string] $OpenCodePath = 'opencode'
)

$ErrorActionPreference = 'Stop'
try {
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
    $runnerPath = Join-Path $repoRoot 'scripts/Invoke-OpenCode.ps1'
    $modelParts = $Model.Split('#')
    if ($modelParts[0] -eq 'deepseek-v4.1-flash') {
        $modelParts[0] = 'opencode-go/deepseek-v4.1-flash'
    }
    if ($modelParts.Count -gt 2 -or $modelParts[0] -notmatch '^[^/#\s]+/[^/#\s]+$' -or
        ($modelParts.Count -eq 2 -and $modelParts[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$')) {
        throw 'Model must be provider/model, optionally followed by #variant.'
    }
    if ($Effort -and $modelParts.Count -eq 2 -and $modelParts[1] -cne $Effort) {
        throw 'Effort conflicts with the variant already present in Model.'
    }
    $resolvedModel = $modelParts -join '#'
    if ($Effort -and $modelParts.Count -eq 1) { $resolvedModel += "#$Effort" }

    $workspace = Get-Item -LiteralPath $WorkingDirectory
    if (-not $workspace.PSIsContainer -or $workspace.PSProvider.Name -ne 'FileSystem') {
        throw 'WorkingDirectory must be a filesystem directory.'
    }
    if ($PSCmdlet.ParameterSetName -eq 'File') {
        $Prompt = Get-Content -LiteralPath $PromptFile -Raw -Encoding utf8
    }
    if ([string]::IsNullOrWhiteSpace($Prompt)) { throw 'Task prompt must not be empty.' }
    if (-not $OutputPath) {
        $runDirectory = Join-Path $repoRoot ('artifacts/opencode-delegation/task-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $runDirectory | Out-Null
        $OutputPath = Join-Path $runDirectory 'events.jsonl'
    } else {
        $OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    }
    [Console]::Error.WriteLine("OpenCode model: $resolvedModel")
    [Console]::Error.WriteLine("OpenCode events: $OutputPath")
    $runnerArguments = @{
        Prompt = $Prompt
        Model = $resolvedModel
        WorkingDirectory = $workspace.FullName
        TimeoutSeconds = $TimeoutSeconds
        Auto = $Auto
        OutputPath = $OutputPath
        OpenCodePath = $OpenCodePath
        PassThru = $true
        FinalOnly = $true
    }
    $LASTEXITCODE = 0
    & $runnerPath @runnerArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
