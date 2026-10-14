# GitHub work tracking

GitHub Issues in `reteter/vidsc` is the work ledger. The implementation plan defines requirements and acceptance contracts; validation documents preserve evidence; the progress document is a short historical ledger with links. Do not maintain duplicate task status in Markdown. Existing evidence remains valid until a relevant change or failure justifies repeating verification.

## Classification

Each open issue has exactly one type and status. Add relevant areas, a milestone for its plan stage, and optional priority. Native GitHub open/closed state records completion; there is no `status:done` label.

| Group | Values |
| --- | --- |
| `type:` | `task`, `bug`, `todo`, `validation`, `research`, `decision`, `epic` |
| `area:` | `playback`, `timeline`, `media`, `export`, `persistence`, `analysis`, `delivery`, `ci`, `docs`, `hooks` |
| `status:` | `backlog`, `ready`, `in-progress`, `blocked`, `deferred` |
| `needs:` | `user-signal` |
| `priority:` | `high`, `normal`, `low` |

`todo` captures candidate work needing refinement; convert it in place to `task` when scoped. `research` is a bounded experiment with a question and exit criteria. `decision` records a choice and rationale. `epic` groups work and is refined into linked issues before execution. A hook is an integration area, so a hook issue can also be a bug, task or validation.

`backlog` is not yet ready; `ready` is scoped, unblocked and eligible within current authorization; `in-progress` means active work; `blocked` names a concrete dependency; `deferred` means deliberately postponed. Replace the old status on transitions. `needs:user-signal` is an execution restriction independent of priority or milestone. Remove it only after explicit user authorization. All long measurements and active-window tests remain deferred until that signal. Later-stage epics do not authorize starting M2 or later work.

Milestones follow M1, M2, M3, M4a, M4b, M5a, M5b, M6, M7 and M8. No artificial due dates. Close a milestone only after its acceptance gate is evidenced, not merely because its current issue list is empty. Link dependencies explicitly; milestone numbering alone does not define the execution sequence.

Default GitHub labels are retained for compatibility, but use the prefixed vocabulary for project work. Add new labels only for a concrete retrieval need.

## Issue content and lifecycle

Use English. Keep the body proportional to the work, with a goal, scope, acceptance checklist, dependencies, minimal verification and any execution restrictions. Link plan sections and evidence instead of copying entire specifications. For bugs include the trigger, expected/actual behavior and eventual root cause. For deferred work state what allows resumption.

1. Read the relevant issue including comments and check for duplicates before creating one.
2. Confirm scope and restrictions, then change its single status to `in-progress` when work starts.
3. Record material findings, blockers and new tasks in issues. Do not post routine command transcripts.
4. Run the smallest relevant verification and update durable evidence when warranted.
5. Commit significant verified work locally using `Refs #N`. Push requires a separate user request.
6. Post the verification result, evidence and commit ID, explicitly saying if the commit is local/unpublished. Close only when acceptance criteria are met; remove status labels. Use the not-planned closure reason for rejected/duplicate work and link any replacement. Reopening requires a status label again.

An issue may be completed locally before publication; this must be explicit in its closing record. Do not use auto-closing commit keywords to imply an unpublished change is already on GitHub. If connectivity fails, continue independent authorized work and report the exact pending synchronization.

## Low-ceremony gh recipes

Use the existing authenticated `gh`; no wrapper, new runtime dependency, token extraction, or global configuration is needed. Run PowerShell without its profile (`login:false` in the command tool). Always specify this repository.

```powershell
gh issue list --repo reteter/vidsc --state open --limit 100 --json number,title,labels,milestone
gh issue view 21 --repo reteter/vidsc --json number,title,body,labels,milestone,state,comments
gh issue list --repo reteter/vidsc --label status:ready --limit 100
gh issue list --repo reteter/vidsc --label needs:user-signal --limit 100
```

Replace example issue numbers with the actual target. For a complete large inventory use paginated API reads rather than silently trusting a truncated list:

```powershell
gh api --paginate 'repos/reteter/vidsc/issues?state=open&per_page=100'
```

The REST issues endpoint also returns pull requests; exclude records with `pull_request` when counting issues.

For writes, create a uniquely named literal UTF-8 file under ignored `artifacts` with `apply_patch`. Markdown content, including backticks, `$()`, quotes and newlines, must never pass through shell interpolation. Use a new payload file per operation rather than reusing a stale one.

```powershell
gh issue create --repo reteter/vidsc --title 'Concise task title' --label type:task --label area:docs --label status:backlog --body-file artifacts/issue-new.md
gh issue edit 21 --repo reteter/vidsc --body-file artifacts/issue-21-body.md
gh issue comment 21 --repo reteter/vidsc --body-file artifacts/issue-21-result.md
gh issue edit 21 --repo reteter/vidsc --remove-label status:backlog --add-label status:in-progress
```

Read the current labels first so the removed status is correct. For one structured update, author a JSON payload with `apply_patch` and use the REST endpoint:

```powershell
gh api repos/reteter/vidsc/issues/21 --method PATCH --input artifacts/issue-21-update.json
```

Example payload (replace the complete label set with the target's intended labels):

```json
{
  "body": "## Goal\nLiteral Markdown with `backticks` and $().\n",
  "labels": ["type:task", "area:docs", "status:in-progress"]
}
```

The `labels` field replaces the label set; preserve unrelated labels. Omit fields not being changed. Read back the affected issue after a write and confirm its content/status. On an ambiguous failed create, search before retrying to avoid duplicates. For a network sandbox restriction, request the narrow required tool escalation; do not alter authentication or global settings.

## Initial migration

The initial import tracks B1-B6 individually, remaining bounded visual acceptance, the M1 retrospective, three timeline TODOs, and later-stage epics. Accepted presentation/lifecycle/CI work is referenced through existing evidence instead of recreated as synthetic closed issues. No tests are triggered by creating this backlog.
