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
5. Commit significant verified work using `Refs #N`, push its feature branch and create/update the PR under standing authorization.
6. Post verification, evidence, commit and PR links. Keep code/document delivery issues open until merge, with `status:in-progress` and an explicit awaiting-user-merge note. Use `Closes #N` in the PR when merging completes the issue. Evidence-only tasks can close upon acceptance. Remove status labels after closure; use not-planned for rejected/duplicate work and link any replacement. Reopening requires a status label again.

The initial migration issue #21 was closed under the earlier local-completion policy; retain that historical record. New delivery issues follow the merge-based closure rule above. If connectivity fails, continue independent authorized work and report the exact pending synchronization.

## Branches and pull requests

The user grants standing authorization to create/maintain branches, commit, push normally to origin and open/update PRs for significant verified increments. This replaces the earlier requirement to ask before every push. A significant increment is a coherent, reviewable bug fix, feature, validation outcome or workflow/documentation change; do not create a PR per trivial edit or wait for an entire milestone.

1. Inspect status, current branch, relevant issue and open PRs. Fetch origin and confirm the default base (currently `master`). Start `codex/<issue-or-topic>` from current `origin/master`. Preserve existing changes/commits; use a separate checkout when needed. Never reset unrelated work.
2. Reuse one branch and PR for one coherent scope. Additional review fixes go into that branch. Start independent work from the default branch; make dependent/stacked work explicit rather than silently carrying another PR's changes.
3. Verify the smallest relevant scope and inspect the final diff. Documentation-only changes use diff/link checks locally; hosted CI still runs. Follow docs/ci.md for code/script/dependency changes. Host tests remain deferred until the user resumes them.
4. Push with upstream tracking and create a PR against the confirmed base. Use a draft while acceptance is incomplete; a normal PR when ready for review. Describe the problem/result, linked issues, verification and material limits. Attach every PR to the current Codex chat. Never push directly to master or force-push.
5. Follow current-head CI and review results in the active session. Fix in-scope failures and real findings, reply with evidence and resolve addressed threads. Re-run only affected checks or justified failures. Use PR Completion's watcher when using its completion workflow; preserve its cursor/output under ignored artifacts. Do not claim a reviewer approved merely because checks are green. Do not silently add reviewer bots, protection rules or schedules.
6. At readiness, report the URL, head SHA, CI/review evidence and remaining limits, then await the user's merge decision. No merge, auto-merge or queue enrollment is allowed without explicit approval for that PR and head. Any new head invalidates approval. The user may merge in GitHub; if asking the agent to land, PR Completion's validated landing helper performs the protected action after approval. Never bypass protections or use admin privileges.
7. Once GitHub confirms merge, fetch and fast-forward the local default branch only when safe. Remove a task branch only after confirming its work is merged and no other checkout/work depends on it; use safe local deletion, never forced deletion. Remote deletion requires the user's cleanup instruction or repository auto-delete policy. Confirm issue closure and remove obsolete status labels.

Code Review provides PR inspection, diffs, checks and private review assistance. PR Completion coordinates preparation, CI/review repair and conflicts. Neither plugin supplies the user's merge decision. Routine PR follow-up in the active session is authorized; recurring/background monitoring requires a separate request. If a deferred host test is necessary for the change, keep the PR draft or explicitly blocked instead of inferring acceptance from CI.

## Connector-first integration

Prefer structured GitHub connector operations for supported issue/PR reads and writes, selecting the `reteter` account for this repository. Bodies are passed directly as data, avoiding shell quoting and temporary payload files. Use `gh` for unsupported operations, GitHub API gaps and workflows that explicitly require the CLI (including PR Completion). Git operations remain local Git commands. Do not duplicate a successful write through both paths; on an ambiguous response, read back before retrying.

## Low-ceremony gh recipes

When CLI fallback is needed, use the existing authenticated `gh`; no wrapper, new runtime dependency, token extraction, or global configuration is needed. Run PowerShell without its profile (`login:false` in the command tool). Always specify this repository.

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
