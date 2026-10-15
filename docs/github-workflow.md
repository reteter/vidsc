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

`backlog` is not yet ready; `ready` is scoped, unblocked and eligible within current authorization; `in-progress` means active work; `blocked` names a concrete dependency; `deferred` means deliberately postponed. Replace the old status on transitions. `needs:user-signal` is an execution restriction independent of priority or milestone. Record the restriction and resumption condition in the issue; remove it only after explicit user authorization. Creating an epic or milestone does not authorize starting its work.

Milestones follow the implementation plan's acceptance stages. No artificial due dates. Close a milestone only after its acceptance gate is evidenced, not merely because its current issue list is empty. Link dependencies explicitly; milestone numbering alone does not define the execution sequence.

Default GitHub labels are retained for compatibility, but use the prefixed vocabulary for project work. Add new labels only for a concrete retrieval need.

## Issue content and lifecycle

Use English. Keep the body proportional to the work, with a goal, scope, acceptance checklist, dependencies, minimal verification and any execution restrictions. Link plan sections and evidence instead of copying entire specifications. For bugs include the trigger, expected/actual behavior and eventual root cause. For deferred work state what allows resumption.

1. Read the relevant issue including comments and check for duplicates before creating one.
2. Confirm scope and restrictions, then change its single status to `in-progress` when work starts.
3. Record material findings, blockers and new tasks in issues. Do not post routine command transcripts.
4. Run the smallest relevant verification and update durable evidence when warranted.
5. Commit significant verified work using `Refs #N`, push its feature branch and create/update the PR under standing authorization.
6. Post verification, evidence, commit and PR links. Keep code/document delivery issues open until merge, with `status:in-progress` and an explicit awaiting-user-merge note. Use `Closes #N` in the PR when merging completes the issue. Evidence-only tasks can close upon acceptance. Remove status labels after closure; use not-planned for rejected/duplicate work and link any replacement. Reopening requires a status label again.

Preserve historical closure evidence when workflow policy changes; do not rewrite past results as if the new policy had always applied. If connectivity fails, continue independent authorized work and report the exact pending synchronization.

## Branches and pull requests

The user grants standing authorization to create/maintain branches, commit, push normally to origin and open/update PRs for significant verified increments. A significant increment is a coherent, reviewable bug fix, feature, validation outcome or workflow/documentation change; do not create a PR per trivial edit or wait for an entire milestone.

1. Inspect status, current branch, relevant issue and open PRs. Fetch origin and confirm the default base (currently `master`). Start `codex/<issue-or-topic>` from current `origin/master`. Preserve existing changes/commits; use a separate checkout when needed. Never reset unrelated work.
2. Reuse one branch and PR for one coherent scope. Additional review fixes go into that branch. Start independent work from the default branch; make dependent/stacked work explicit rather than silently carrying another PR's changes.
3. Verify the smallest relevant scope and inspect the final diff. Documentation-only changes use diff/link checks locally; hosted CI still runs. Follow docs/ci.md for code/script/dependency changes. Honor execution restrictions recorded in the issue and user instructions.
4. Push with upstream tracking and create a PR against the confirmed base. Use a draft while acceptance is incomplete; a normal PR when ready for review. Describe the problem/result, linked issues, verification and material limits. Attach every PR to the current Codex chat. Never push directly to master or force-push.
5. Follow current-head CI and review results in the active session. Fix in-scope failures and real findings, reply with evidence and resolve addressed threads. Re-run only affected checks or justified failures. Use PR Completion's watcher when using its completion workflow; preserve its cursor/output under ignored artifacts. Do not claim a reviewer approved merely because checks are green. Do not silently add reviewer bots, protection rules or schedules.
6. At readiness, report the URL, head SHA, CI/review evidence and remaining limits, then await the user's merge decision. No merge, auto-merge or queue enrollment is allowed without explicit approval for that PR and head. Any new head invalidates approval. The user may merge in GitHub; if asking the agent to land, PR Completion's validated landing helper performs the protected action after approval. Never bypass protections or use admin privileges.
7. Once GitHub confirms merge, perform the routine cleanup below under the user's standing cleanup authorization. Safely synchronize the local default branch, remove only the verified merged PR branch locally/from origin, prune tracking refs and reconcile issue closure/statuses. Report any retained work or safe-deletion refusal.

Code Review provides PR inspection, diffs, checks and private review assistance. PR Completion coordinates preparation, CI/review repair and conflicts. Neither plugin supplies the user's merge decision. Routine PR follow-up in the active session is authorized; recurring/background monitoring requires a separate request. If a deferred host test is necessary for the change, keep the PR draft or explicitly blocked instead of inferring acceptance from CI.

## Merge method and squash cleanup

Squash merge is the default: one coherent PR produces one default-branch commit. Keep the PR title
suitable as the final commit subject; its description must retain relevant issue links and verification.
Review the final squash message for privacy and accuracy instead of blindly concatenating intermediate
commit messages. Use a merge commit only when the user explicitly chooses to preserve meaningful
individual commits. Rebase merge is not an automatic alternative. Keep ordinary incremental commits
and pushes on the published feature branch; squash at landing, not by rewriting that branch.

PR Completion already provides the landing mechanism: its take-pr-to-completion skill ships
scripts/pr_land.py with --mode auto --method squash. First obtain a read-only plan without --confirm,
using the exact PR head and the same readiness-policy inputs as the watcher. After explicit user
approval, invoke the validated confirmation flow with the returned policy digest. Auto mode may merge
immediately; selecting squash is not permission to enable auto-merge. If a merge queue is required,
the helper uses --mode queue without --method; verify that the queue policy matches the intended
method and surface any conflict instead of bypassing the queue. Do not add a custom landing script.
If the plugin is unavailable, report that prerequisite and restore it before agent-driven landing;
the user can still choose to merge through GitHub. Do not silently substitute a different mechanism.

### Routine cleanup after an authorized merge

The user grants standing authorization for routine cleanup after a separately authorized PR merge.
Do it in the same session without asking again: synchronize the local default branch, remove that
PR's verified merged feature branch locally and from origin, prune obsolete tracking refs, and
reconcile closed issues. This does not authorize another PR's merge, deletion of unmerged work,
forced deletion, history rewriting, or removal of ignored artifacts and evidence.

1. Confirm GitHub reports `MERGED` for the approved head and record the resulting merge/squash
   commit. Verify every still-existing local/remote feature tip matches that head, with no additional
   commits or dependent worktrees. Preserve changed or unrelated work instead of resetting/stashing it.
2. Fetch without pruning first so an existing upstream reference remains available for safe local
   deletion. With a clean checkout, switch to the local default branch and fast-forward only to its
   refreshed origin counterpart. Verify it contains the confirmed merge result. Start subsequent
   work from this refreshed default, never from the old feature branch.
3. Prefer `git branch -d <exact-feature-branch>` before deleting the remote branch. After squash,
   Git may accept deletion against the still-existing upstream even though the original feature
   commit is not an ancestor of the default branch. That upstream check is not proof of delivery;
   the independent merged-head/result checks above remain mandatory.
4. Check the remote tip again and delete only the exact merged feature branch, then fetch/prune
   origin. If GitHub already deleted it, prune its stale tracking reference. Never delete a branch
   that has advanced beyond the approved head. If safe local deletion refuses, retain that branch
   and report it; do not force deletion or manufacture ancestry/upstream state to bypass the refusal.
5. Confirm delivery issues closed and remove only obsolete `status:` labels. Close a milestone only
   when its documented acceptance gate is evidenced. Report the final default-branch commit,
   removed branches and any retained items; distinguish new active PR branches from stale work.

Squash ancestry can still make safe local deletion refuse, particularly when the remote branch was
already removed. A separate explicit decision is required for any forced deletion. Routine cleanup
preserves source media, generated clips, runtime bundles and ignored workflow evidence.

This is a repository workflow default, not a request to disable other methods in GitHub settings.
Repository protections and merge-queue requirements still take precedence over a chosen mechanism;
the user's per-PR approval is always required.

## Documentation during other work

Choose branches by coherent scope, not by file extension. There is no permanent documentation branch and no mandatory separate PR for Markdown changes.

| Change | Branch and PR |
| --- | --- |
| Feature/fix instructions, contracts, validation evidence or its progress entry | The implementation branch and PR; keep code and its explanation consistent |
| Review correction or clarification of an open PR | That same branch and PR, even if the correction changes only documentation |
| Independent repository policy, general guide or unrelated documentation repair | A separate `codex/<issue-or-topic>` branch from freshly fetched `origin/master` |
| Small wording correction in text already changed for the task | Include it in the current PR; avoid unrelated document cleanup |
| Newly discovered unrelated TODO | Record an issue immediately; implement separately when appropriate |

If another branch is active, prefer a separate worktree for independent documentation work. Leave the active checkout, index and uncommitted changes untouched. Do not create the documentation branch from the feature head or copy entire modified files from that checkout; apply only the independent changes to the default-branch version. Switching branches is acceptable only with a clean checkout and no other task using it. Do not automatically stash/reset another task's work.

If independent documentation edits have already been mixed into a feature file, inspect the exact diff first. Transfer only clearly separable hunks to the independent branch and verify both diffs. Preserve all other edits; if ownership or dependencies are unclear, keep the work intact and clarify rather than guessing. A separate commit on the feature branch alone does not isolate a new PR's base.

Documentation that depends on unmerged implementation stays with that implementation. Use a stacked PR only when explicitly justified and document its base/dependency; do not silently present it as an independent documentation change. After a policy PR merges, update ongoing branches from the default branch when needed, using a normal merge for published branches. Apply the user's current instructions immediately, even while their documentation PR awaits merge; do not claim the policy is already on origin/master.

Update the progress ledger alongside the change it describes, using compact entries to reduce conflicts. Resolve overlapping ledger/AGENTS changes by preserving both valid decisions, not by choosing an entire file from one side. Issue status remains authoritative and can be updated directly without a documentation branch.

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
