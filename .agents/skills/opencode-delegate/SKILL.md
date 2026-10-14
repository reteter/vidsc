---
name: opencode-delegate
description: Delegate coding, analysis, or review tasks to separate OpenCode headless runs when the user requests OpenCode workers or subagents, including a chosen model and reasoning effort. Coordinate task ownership, collect results, and verify their integration in this project.
---

# Delegate through OpenCode

Use this project's PowerShell helper, which forwards to an external OpenCode
runner, to launch independent OpenCode workers. The parent agent owns
decomposition, integration, and verification. These workers are external OpenCode
sessions, not another harness's sidebar chats or native collaboration agents.
Do not create sidebar chats to perform this workflow.

## Resolve the request

Interpret requests such as:

> Deleguj subagentom przez OpenCode - model: deepseek-v4.1-flash, effort: high.

Delegate the current authorized task. Preserve requested analysis-only scope,
worker count limits, model, effort, and exclusions. Choose a useful number of
workers based on independent work; a single task may need only one worker.
Do not start implementation when the user only asks to discuss or prepare tasks.

Resolve the project root from this skill's location: three parents above the
`opencode-delegate` directory (the project root within `artifacts-depo`,
`projects/vidsc`).
Use absolute paths for the helper, workspace, external runner, and artifacts.
The target workspace is the task's directory; default it to the project root
unless the task names another existing directory.

This project's helper is
`.agents/skills/opencode-delegate/scripts/Invoke-OpenCodeTask.ps1`. It forwards
each task to an external OpenCode runner that lives outside this repository and
is not versioned here; on this host that dependency defaults to
`D:\code\opencode\headless\scripts\Invoke-OpenCode.ps1`. The helper resolves and
validates the runner file before launching it and never modifies it. Override the
default with `-RunnerPath <absolute path>` when the runner is installed
elsewhere. Read the external runner's `-Help` when its parameters are unfamiliar.

Before the first run in a session, check `opencode --version` and
`opencode run --help`. This skill targets V2; do not apply V1 flags.

Default model: `opencode-go/deepseek-v4.1-flash`. In this project, resolve the
short name `deepseek-v4.1-flash` to that identifier unless the user specifies a
provider. Resolve other short names from the available catalog; do not guess.

Effort is optional and has no implicit default. OpenCode V2 selects catalog
variants through `provider/model#variant`; the runner has no separate effort
parameter. The local test on OpenCode V2.0.24 confirmed that this project's
`deepseek-v4.1-flash` + `effort: high` resolves to
`opencode-go/deepseek-v4.1-flash#high`; use that mapping without asking again
when the same provider/model is requested. For other combinations, inspect
current model metadata or official model documentation. `high` is not
universally supported. If the model already includes a variant, reconcile it
with the requested effort. Never silently drop effort, fabricate a variant,
change provider, or change model. If mapping cannot be established, explain the
unresolved setting and obtain the user's choice before launching dependent work.

The real documentation run completed with `#high`; the sanitized session export
recorded `info.model.variant = "high"` and the same variant in assistant messages.
An omitted variant means configuration/provider defaults, not a known effort.
Empty local catalog responses do not prove a variant is unavailable. If the
user explicitly chooses a variant, try that exact selection and report a
resolution error rather than silently falling back. Model reference:
<https://opencode.ai/v2/docs/models>.

## Prepare worker assignments

Record the initial Git status so existing user changes are preserved. For each
worker, specify its goal, required context, absolute working directory, owned
files/modules, dependencies, acceptance criteria, and expected report. Provide
actual task text and relevant contracts: workers do not inherit this chat.

Tell every worker that it is not alone, must preserve others' edits, and must
stay within its ownership. Include applicable repository instructions and ask
it to read applicable `AGENTS.md`. Prohibit commit, push, unrelated changes, and
global configuration edits unless separately authorized.

Recursive delegation is explicitly authorized in this user's workflow: a worker
may launch further OpenCode subagents when that helps its assigned task. State
this authorization in the worker's prompt together with the same ownership,
no-commit, and no-external-runner-change constraints, so nested workers stay in
scope. Do not add a blanket prohibition on nested delegation.

Do not pass credentials or unrelated private conversation content in prompts.

For documentation tasks, require source-based claims, explicit distinction
between implemented behavior and placeholders, valid relative links, and clear
runtime verification limits. State the artifact/report language explicitly.
Do not require a build or GUI launch for a documentation-only task unless needed
to resolve a concrete claim.

Independent analysis or edits to disjoint files can run concurrently. Sequence
dependent work; use isolated worktrees when edit ownership cannot be separated.
Do not run concurrent builds against the same output directories. Publish
contracts needed by dependent workers before starting them.

If the project provides a delegation task document, read it from the project root
and respect its ownership and dependency ordering. Do not launch those tasks
merely because this skill mentions them.

## Launch and monitor

This user explicitly selected `-Auto` for delegated OpenCode runs. Supply it by
default in this project unless the user disables it for a run. Explicit OpenCode
deny rules still apply. This setting does not approve sandbox-permission
escalation.

Launch the runner outside the Codex sandbox through the supported approval
mechanism: `exec_command` with `sandbox_permissions: "require_escalated"` and
a concrete justification. Do not alter sandbox configuration or ACLs. If approval
is rejected, report the reason and do not bypass it. `--standalone` isolates the
OpenCode server, not its filesystem or user credentials.

Store each assignment as a UTF-8 text file under a unique directory in this
project's ignored log area, `artifacts/logs/opencode-delegation/`. The repository
root `.gitignore` ignores any `logs/` directory but does not ignore `artifacts/`,
so this path is ignored without editing `.gitignore`. Use a distinct JSONL file
per worker; the runner overwrites `-OutputPath`. Use the project helper
`.agents/skills/opencode-delegate/scripts/Invoke-OpenCodeTask.ps1`. It accepts
`-Prompt` or UTF-8 `-PromptFile`, requires `-WorkingDirectory`, and accepts
`-Model`, `-Effort` (alias `-Variant`), `-TimeoutSeconds`, `-RunnerPath`,
`-OutputPath`, and `-OpenCodePath`. It resolves the local DeepSeek short name,
rejects conflicting model/effort suffixes, enables `-Auto` by default, and
returns final text on stdout with diagnostics on stderr. Use `-Auto:$false` to
disable auto-approval. `-TimeoutSeconds` defaults to `0`, which disables the
timeout: the external runner applies a limit only when the value is greater than
`0`. In this project the default is unlimited for all subsequent runs; pass a
finite value only when the user explicitly requests a bounded run. Without
`-OutputPath`, the helper creates a unique
`task-<guid>/events.jsonl` under the ignored log area and prints that path on
stderr. Explicit output paths retain runner overwrite rules and require an
existing parent directory. Relative input/output paths resolve against the
caller's directory, not the target workspace. The helper does not grant sandbox
permissions, discover variant support, or verify session exports.
Example PowerShell command after creating a run directory and writing the
assignment:

```powershell
& 'D:\code\codex\artifacts-depo\projects\vidsc\.agents\skills\opencode-delegate\scripts\Invoke-OpenCodeTask.ps1' `
    -PromptFile 'D:\code\codex\artifacts-depo\projects\vidsc\artifacts\logs\opencode-delegation\run-unique\worker-1.txt' `
    -Model 'deepseek-v4.1-flash' -Effort high `
    -WorkingDirectory 'D:\code\codex\artifacts-depo\projects\vidsc' `
    -Auto -TimeoutSeconds 0 `
    -RunnerPath 'D:\code\opencode\headless\scripts\Invoke-OpenCode.ps1' `
    -OutputPath 'D:\code\codex\artifacts-depo\projects\vidsc\artifacts\logs\opencode-delegation\run-unique\worker-1.jsonl'
```

Adapt paths and the resolved model/variant to the actual assignment. Load prompt
files as data; do not interpolate their contents into shell command strings. The
default `-TimeoutSeconds 0` means unlimited for all subsequent runs in this
project; pass a finite value only when the user explicitly requests a bounded
run. The example selects high explicitly; omit the suffix only when no effort or
variant was requested.

Start long commands with a short yield and retain their returned command-session
IDs. Use `write_stdin` to collect completion later while performing independent
parent work. Inspect JSONL tails for live progress: console output is buffered
until the run finishes. `-PassThru -FinalOnly` returns the final text while the
file retains all events. Diagnostics remain on stderr. Keep progress updates
concise and do not dump entire transcripts into the conversation.

Track each worker's assignment, resolved model/variant, session ID, artifact
paths, and outcome. This version operates during an active turn; it does not
provide detached jobs, restart persistence, session continuation, or scheduling.
The command-session ID returned by `exec_command` and OpenCode's `ses_...` ID
are different: the former is for polling the process, the latter for export/API.

## Verify the model selection

When effort/variant is requested, confirm the selected model using a sanitized
session export, not the worker's self-report or the launch argument alone.
Read [references/session-verification.md](references/session-verification.md)
for the tested V2.0.24 procedure. The JSONL events expose `sessionID` but the
observed `step_start`, `text`, and `tool_use` events do not identify the variant.
Check the export early when possible, then refresh it after completion. This
verifies OpenCode's recorded selection, not the provider's internal reasoning.
If export is unavailable, distinguish the requested variant from the unverified
effective selection in the report; never label it confirmed.

JSONL and sanitized export provide progress and stored conversation metadata,
not the full internal model context. Sanitization redacts text and file data;
keep any exports in ignored artifacts and avoid displaying whole transcripts.

## Collect and verify

Wait for all required workers. A command yield is not completion. A nonzero exit,
timeout, malformed JSON, or missing final response is a failed/incomplete run.
Inspect preserved partial events and workspace changes before retrying: a worker
may have edited files before failure. Retry only after an actionable cause is
identified, with a bounded attempt; do not repeatedly rerun a failing assignment.

Review worker reports and diffs against ownership and acceptance criteria.
Integrate dependencies, resolve conflicts while preserving existing work, and
run the smallest meaningful verification. A successful process or worker claim
does not establish correctness. Distinguish automated checks from manual GUI
checks and report unverified behavior.

Record process exit code, requested model/effort, verified provider/model/variant,
changed files, and checks. Compare the final working tree to the initial status;
include newly created files in review, since ordinary `git diff` omits them.
For documentation, check relative links and spot-check important claims against
source rather than treating the worker's successful exit as acceptance.

If the user requests a baseline commit, create it before launching workers.
If the user requests discarding a test, restore its tracked files and remove only
its identified untracked outputs after validating absolute cleanup targets.
Do not delete unrelated or ignored artifacts with a blanket clean command.

Summarize outcomes, checks, failures, and limitations to the user in Polish;
write prompts, code, comments, and project artifacts in English. Report the actual
model/variant used. Keep run transcripts in ignored artifacts and commit or
publish only when requested.
