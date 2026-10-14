# Verify an OpenCode model and variant

Tested locally with OpenCode V2.0.24 and
`opencode-go/deepseek-v4.1-flash#high`. The export has a root `info` object and
`messages` array. Model fields are `providerID`, `id`, and `variant` in both
`info.model` and assistant-message `model` objects. Treat this as a versioned
contract; inspect current help/schema if the shape changes.

## Export

Use the same target working directory as the worker. Run
`opencode session export --help` first if the flags have not been checked, then:

```powershell
$runDirectory = 'D:\code\codex\artifacts-depo\projects\vidsc\artifacts\logs\opencode-delegation\run-unique'
$eventsPath = Join-Path $runDirectory 'worker-1.jsonl'
$sessionID = $null
foreach ($line in [System.IO.File]::ReadLines($eventsPath)) {
    try { $event = $line | ConvertFrom-Json -Depth 100 } catch { continue }
    if ($event.sessionID) { $sessionID = $event.sessionID; break }
}
if (-not $sessionID) { throw 'No OpenCode session ID found in worker events.' }

$exportText = & opencode session export --standalone --sanitize $sessionID
if ($LASTEXITCODE -ne 0) { throw 'OpenCode session export failed.' }
$export = $exportText | ConvertFrom-Json -Depth 100
$exportText | Set-Content -LiteralPath (Join-Path $runDirectory 'worker-1.session-sanitized.json') -Encoding utf8
```

The local test could export a running private-server session by ID from another
`--standalone` invocation. Do not assume this works with another user profile or
data location. Never start or reconfigure the shared service just to verify a run.

## Check the recorded selection

For a request resolved to `opencode-go/deepseek-v4.1-flash#high`:

```powershell
$assistantMessages = @($export.messages | Where-Object type -eq 'assistant')
if ($assistantMessages.Count -eq 0) {
    throw 'No assistant message is available yet; selection is not confirmed.'
}
$recordedModels = @($export.info.model) + @($assistantMessages | ForEach-Object { $_.model })
foreach ($recordedModel in $recordedModels) {
    if ($null -eq $recordedModel -or
        $recordedModel.providerID -cne 'opencode-go' -or
        $recordedModel.id -cne 'deepseek-v4.1-flash' -or
        $recordedModel.variant -cne 'high') {
        throw 'Recorded model or variant does not match the requested selection.'
    }
}
'Confirmed recorded selection: opencode-go/deepseek-v4.1-flash#high'
```

Adapt expected values for other requests. Check assistant messages as well as the
session default: a session-level selection alone is weaker evidence of which
model actually produced an answer. Refresh the export after completion so the
check includes every assistant message. Export failure is a verification failure,
not automatically a failure of the worker's task.

Record the evidence path and verification outcome in the parent report. Tokens,
cost, tool activity, and textual statements such as "I used high" do not prove
a reasoning effort. A recorded `variant: high` establishes the OpenCode selection;
it does not independently prove how the provider applied that setting.
