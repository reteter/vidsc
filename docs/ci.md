# Continuous integration

The repository uses `.github/workflows/ci.yml` on a Windows hosted runner. It installs the SDK and roll-forward policy specified by `global.json`, then calls the same entry point used locally:

```powershell
pwsh -NoProfile -File tools/Test-CI.ps1
```

The script resolves the project root from its own location, so it also works when invoked from another directory. It checks tracked PowerShell scripts with the PowerShell parser, runs the existing isolated promotion/rollback scenarios, restores the solution, compiles the entire solution in Release, and runs all existing unit tests. It does not require media files, packaged FFmpeg/Python runtimes, GPU access or an interactive desktop. The WPF view-model tests use their existing test doubles; this is not a playback run.

Each invocation keeps a unique `artifacts/ci/<run>/` directory containing command logs, the build binary log, transcript, failure details and TRX results when tests execute. The workflow uploads this directory even after a failed check; stages that did not run have no results. Logs are retained for 14 days. Runs have a 25-minute timeout, superseded runs on the same branch are cancelled, and the token has read-only repository contents access. No dependency cache or lock-file policy is introduced.

A green CI run establishes compilation, unit checks and promotion rollback behavior. It does not establish a runnable Release bundle, native dependency packaging, presented-frame correctness or performance/resource acceptance. Windows/native checks remain separate from CI; consult the relevant issues for execution restrictions. `tools/Build-Release.ps1` and `run.bat` retain their build, bundle and launch roles.

Locally, run the smallest relevant tests after behavior changes; run this full entry point for CI/script changes, dependency or solution changes, or before publishing a branch with code changes. A passing run need not be repeated without another change or unresolved failure. Use the existing Release/native validation only when changing playback, GPU integration, media jobs, packaging or lifecycle behavior that those checks actually cover. Documentation-only branches need local diff/link checks, not a build; normal hosted CI still runs on push/PR. Do not run resource-heavy local build/test checks concurrently against the same checkout.

## Efficient media verification

Before expanding an integration, choose the highest-risk backend contract and a small test with
explicit acceptance criteria, non-goals and a time/run budget. Do not grow a generic harness while
the contract remains unproven. Count every attempt and classify its outcome as environment,
fixture/harness or product evidence. A harness fix or renamed scenario does not reset the budget.
At the limit, summarize evidence, state the next hypothesis and justify a new bounded budget before
continuing within the authorized scope.

Qualify the fixture and harness separately before trusting the product oracle: verify required input
properties and exercise the measurement path with a small known result. Acceptance must include a
real adapter invocation in the target runtime (the packaged runtime when packaging is part of the
contract), not only parser or mocked results. Cover a minimal successful lifecycle and a relevant
failure path, including cleanup and resource release where applicable.

For an API/schema or runtime contract migration, identify all consumers before implementation:
producer/driver, parser, callers, tests, packaging gates and documentation. Check the affected
boundaries together so a passing producer test cannot hide a stale consumer or launch contract.

After a fix, run the affected scenario first. Broad suites belong at stable checkpoints, when
required by the CI contract, or when a named cross-cutting risk justifies them. A harness change
needs its own small check before being used to certify product behavior.

Before visible-frame runs, verify the render surface, visibility/focus/occlusion assumptions and
availability/freshness of required telemetry. An unavailable measurement is not product acceptance
or automatically a playback defect. Diagnose the harness separately.

Run long measurements only when authorized, on a frozen build identified by commit and tool/runtime
manifest. Do not compile, replace binaries or edit measured code during the run, including from
another agent or checkout sharing those outputs. Record storage/cache state and scenario counts.
Do not shorten frozen acceptance durations and label the result a full pass.

## Public evidence

Review uploaded logs, TRX/build logs, screenshots and attachments as public output, even if their
local directory is ignored by Git. Hosted CI must use synthetic/non-private inputs. Keep local media
paths, personal identifiers and raw host reports in ignored artifacts; publish neutral fixture IDs,
necessary technical properties and sanitized result summaries. Never copy credentials into reports.

## Hosted verification

The first import and the Node 24 follow-up both passed on GitHub. The final workflow uses
`checkout@v6`, `setup-dotnet@v5` and `upload-artifact@v6`; each declares the Node.js 24 runtime.
[Run 38606473676](https://github.com/reteter/vidsc/actions/runs/38606473676) verifies commit
`ee711e7`: all 241 unit tests, promotion rollback, compilation and artifact upload passed.
The Node 20 deprecation annotation is gone. The existing unused vendor padding-field compiler
warnings remain unrelated to the Actions runtime. Local report copies are under ignored
`artifacts/ci/github-38606473676/`.
