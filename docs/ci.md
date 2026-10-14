# Continuous integration

The standalone repository uses `.github/workflows/ci.yml` on a Windows hosted runner. It installs the SDK specified by `global.json` (currently 10.0.201, with the existing roll-forward policy), then calls the same entry point used locally:

```powershell
pwsh -NoProfile -File tools/Test-CI.ps1
```

The script resolves the project root from its own location, so it also works when invoked from another directory. It checks tracked PowerShell scripts with the PowerShell parser, runs the existing isolated promotion/rollback scenarios, restores the solution, compiles the entire solution in Release, and runs all existing unit tests. It does not require media files, packaged FFmpeg/Python runtimes, GPU access or an interactive desktop. The WPF view-model tests use their existing test doubles; this is not a playback run.

Each invocation keeps a unique `artifacts/ci/<run>/` directory containing command logs, the build binary log, transcript, failure details and TRX results when tests execute. The workflow uploads this directory even after a failed check; stages that did not run have no results. Logs are retained for 14 days. Runs have a 25-minute timeout, superseded runs on the same branch are cancelled, and the token has read-only repository contents access. No dependency cache or lock-file policy is introduced.

This workflow becomes active when vidsc is the repository root; GitHub does not discover this nested workflow while it remains under `projects/vidsc` in the depot. Copy the whole project tree, including `.github`, without adding depot-relative paths to the workflow.

A green CI run establishes compilation, unit checks and promotion rollback behavior. It does not establish a runnable Release bundle, native dependency packaging, presented-frame correctness, B1–B6 performance/resource acceptance or M1 completion. Those existing Windows/native checks remain separate and deferred in CI. `tools/Build-Release.ps1` and `run.bat` retain their existing build, bundle and launch roles.

Locally, run the smallest relevant tests after behavior changes; run this full entry point for CI/script changes, dependency or solution changes, or before publishing a branch. A passing run need not be repeated without another change or unresolved failure. Use the existing Release/native validation only when changing playback, GPU integration, media jobs, packaging or lifecycle behavior that those checks actually cover. Documentation-only changes normally need no build. Do not run resource-heavy local build/test checks concurrently against the same checkout.

## Hosted verification

The first import and the Node 24 follow-up both passed on GitHub. The final workflow uses
`checkout@v6`, `setup-dotnet@v5` and `upload-artifact@v6`; each declares the Node.js 24 runtime.
[Run 38606473676](https://github.com/reteter/vidsc/actions/runs/38606473676) verifies commit
`ee711e7`: all 241 unit tests, promotion rollback, compilation and artifact upload passed.
The Node 20 deprecation annotation is gone. The existing unused vendor padding-field compiler
warnings remain unrelated to the Actions runtime. Local report copies are under ignored
`artifacts/ci/github-38606473676/`.
