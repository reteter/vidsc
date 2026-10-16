# vidsc — repository instructions

## Scope and sources of truth

- Work in this repository or its owned worktrees. Origin: https://github.com/reteter/vidsc.git.
- GitHub Issues holds current scope, status, dependencies and deferrals; PRs hold proposed changes, reviews and merge state.
- docs/implementation-plan.md defines requirements and acceptance contracts. docs/implementation-progress.md records verified history; docs/validation/ holds evidence. Do not duplicate current task status or test counts here.

## Session start — main agent

1. Read docs/implementation-progress.md and docs/ci.md.
2. Inspect Git status, branch/upstream, recent commits and worktrees. Fetch origin before selecting a base or assessing freshness.
3. Read the selected issue, comments and related PR. If none is selected, inspect open issues/PRs and choose only eligible work within the authorized scope.
4. Read docs/github-workflow.md before issue/branch/PR lifecycle operations. Read other documentation only as needed; do not repeat full research or accepted suites without a concrete reason.
5. Briefly confirm the state and next action. If no work is eligible, report the blocker instead of expanding scope.

## Orchestration

- The main agent owns integration, verification, issue updates and delivery. Delegate independent subtasks only when worthwhile.
- Built-in subagents: gpt-6.1-sol, low effort for implementation/tests; high effort for design/review. Disclose unavailability before substituting.
- Give subagents bounded responsibilities, file ownership and applicable constraints. They read task-specific context rather than repeat the main agent's startup discovery. Delegate cohesive contracts or phases; keep small corrections with the main agent when handoff costs exceed the benefit.
- Before delegation, define acceptance criteria, non-goals and an experiment/time budget. Before parallel implementation, agree the minimal API/schema, source/time identity, resource limits, errors and resource ownership; freeze the proven interface before splitting dependent integration work.
- Focus reviews on the exact delta, named risks and available evidence. Prioritize timing, concurrency and resource ownership for media changes; repeat broader review only when a change invalidates earlier conclusions.
- Require a final subagent report: changed files, acceptance results, checks run/not run, integration risks, scope assessment and one workflow lesson. Include input/output/cached tokens and context state when available; otherwise state unavailable. A handoff is ready for integration, not independently accepted.
- Collect available runtime metrics centrally without extra report-only agent turns. Keep the latest cumulative snapshot per agent/session and phase baselines/deltas; cached tokens are a subset of input, not an extra total. Do not infer remaining context or cost from cumulative counters. Keep a compact current assessment and chronological evidence in ignored workflow logs, including scope decisions and lessons; GitHub remains the task ledger.
- Do not use opencode-delegate without an explicit user request.

## Execution boundaries

- Read execution restrictions and authorized scope from the relevant issues and the user's instructions before starting work. Record deferrals and their resumption conditions in issues, not in this file.
- Work marked deferred or needs:user-signal requires the user's explicit resumption; creating an issue or milestone does not authorize its implementation. If scope or permission is unclear, clarify before the dependent action.
- Generic continuation, issue labels, review comments and green CI do not lift these restrictions or authorize merge.
- Use docs/ci.md to choose verification; do not run resource-heavy checks concurrently in one checkout. Do not schedule background monitoring implicitly.
- Prove the riskiest backend contract with a small bounded test before expanding integration or test tooling. After a fix, run its affected scenario; reserve broad suites for a stable checkpoint or a concrete cross-cutting risk. Stop and reassess when the experiment budget is exhausted.
- Before visual tests, check surface visibility, focus/occlusion assumptions and telemetry availability. Distinguish harness/measurement failures from product failures. Run long measurements on an identified frozen build with no concurrent builds or code changes; follow docs/ci.md.

## Public repository privacy

- Treat pushes, issue/PR content, attachments and CI artifacts as public. Review the exact outgoing diff/content before publication; gitignore alone is not a privacy check.
- Never publish credentials, personal identifiers, private source filenames/paths, user media, screenshots of private content or raw host logs. Use neutral fixture IDs and placeholders; retain mappings and raw evidence only in ignored local artifacts. Preserve technical properties needed to interpret results.
- If potentially private data is already published, stop copying it, sanitize tracked content, and tell the user what remains exposed. A cleanup commit does not erase history. Do not rewrite public history, force-push or delete remote evidence without an explicit remediation decision.

## GitHub and delivery

- Track substantial work in an issue. Apply the classification and lifecycle rules in docs/github-workflow.md.
- Prefer the GitHub connector for supported operations on reteter/vidsc using the reteter account; use gh for gaps and plugin-required CLI workflows.
- For CLI writes, create literal UTF-8 payload files under ignored artifacts with apply_patch; use --body-file or gh api --input instead of shell-interpolated bodies.
- Use codex/<issue-or-topic> branches from current origin/master; reuse the branch/PR for the same coherent scope.
- Task-related documentation stays with its implementation. Independent documentation uses a separate default-based branch, preferably an isolated worktree when another task is active.
- Standing authorization covers significant verified commits, ordinary feature-branch pushes, PR creation/updates and in-scope CI/review repairs. Never push directly to master, force-push, rewrite published history or bypass protections.
- Use PR Completion for PR preparation/follow-up and Code Review where useful. Report current-head checks and review evidence.
- Merge, auto-merge and queue enrollment require explicit user approval for the particular PR and current head; new commits invalidate approval.
- Default to squash merge: one coherent PR becomes one default-branch commit. A merge commit is an explicit user-approved exception; do not choose rebase merge automatically. Use PR Completion's validated landing helper with the approved method. After squash, start new work from the refreshed default branch; never force-delete a branch merely because safe deletion rejects it.
- After a confirmed, explicitly authorized merge, perform routine cleanup as standard under the user's standing authorization: safely synchronize the local default branch, remove that PR's merged feature branch locally and from origin, prune stale tracking refs, and reconcile closed-issue statuses. Verify the approved head, merge result and worktree dependencies first; preserve unrelated work and ignored evidence, and report any safe-deletion refusal. Follow docs/github-workflow.md; cleanup authorization never authorizes another PR's merge.
- Keep delivery issues open until merge. Follow the documented issue-closure and branch-cleanup rules.

## Media safeguards and technical context

- vidsc is a Windows x64 C#/.NET/WPF/Flyleaf video navigator and trimmer. Stream copy is an intermediate milestone; accurate cuts with re-encoded boundaries and copied interior remain required.
- Keep source media read-only. Use owned temporary outputs, validation and no-clobber promotion; preserve existing files on failure.
- Never infer exact frames or safe cuts from average FPS, seek completion, key packets or scene markers. Read the relevant media contracts before implementation.
- Keep playback-native and CLI FFmpeg runtime sets separate. No silent full transcode, bit-depth reduction, downmix or subtitle conversion.
- Keep generated media, runtime downloads and large logs in ignored artifacts. Do not change global Git, tool or user settings.

## Read when relevant

- Architecture, timing/edit/export semantics, resource budgets and milestone gates: docs/implementation-plan.md.
- Playback hooks, lifecycle and media correctness evidence: relevant records in docs/validation/ and their implementation references.
- Verification policy: docs/ci.md. Build/delivery entry points: tools/Build-Release.ps1, run.bat and tools/runtime-manifest.json; apply repo-builds when changing delivery workflows.
- Release remains an expanded framework-dependent win-x64 bundle under artifacts/release/app; run.bat rebuilds before launch. Use tools/Test-Delivery.ps1 -NonGui for the desktop-free package gate. See docs/release-guide.md and docs/distribution.md for prerequisites, usage and the private-local-only distribution decision.
- GitHub operations, branch isolation and documentation ownership: docs/github-workflow.md.
- Short continuation prompts: docs/session-start.md.
