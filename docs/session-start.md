# Starting or continuing a session

Open the standalone vidsc repository or the worktree that owns the selected task. AGENTS.md is the durable operating contract; do not paste an old, duplicated ruleset into every session. Current task state comes from GitHub Issues and PRs, historical evidence from the progress ledger, and verification policy from docs/ci.md.

Suggested short prompt (translate to Polish when using it in chat):

```text
Continue vidsc according to AGENTS.md, acting as the orchestrator.
Follow the session-start checklist, briefly confirm the state, and continue
issue #<number> / PR #<number>. Preserve deferred tests and the current scope.
```

When no task has been selected:

```text
Continue vidsc according to AGENTS.md, acting as the orchestrator.
Inspect the current Git, issues and PR state, then choose the next eligible
task within the authorized scope. If nothing is eligible, report the blocker.
```

The repository instructions already specify the two initial documents, focused follow-up reading,
built-in delegation models/efforts, minimum relevant verification, branch/PR authorization and
user-controlled merging. Repeating those details in the prompt increases the chance of stale rules.

Specify only actual overrides: a concrete issue/PR, a changed scope, or explicit permission to resume
particular deferred host scenarios. A generic continuation prompt does not authorize host tests,
M2 implementation or merging. Scope permission does not automatically lift an execution restriction.

Until the workflow PR is merged, start from its branch/worktree to load these instructions, or
explicitly point the new session to that PR for context. Do not create unrelated implementation work
on the workflow branch: follow the documented branch/worktree isolation policy. After merge, use an
up-to-date checkout of the default branch or the existing branch owning the selected task.
