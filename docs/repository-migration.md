# Standalone repository migration

Date: 14 October 2026.

The active project is now `<REPOSITORY_ROOT>`, with origin
`https://github.com/reteter/vidsc.git`. The former depot copy at
`<ARCHIVED_REPOSITORY>` is retained as an archive.
Continue development and the deferred M1 host acceptance in the standalone repository.

## History and source integrity

The project-only history was extracted using `git subtree split` from depot commit `7406e37`.
The split contains 19 commits and its tree matches the depot project exactly. Merge commit
`4ff9693` joins that history with the user's initialization commit `944c6a4`; neither history
was replaced. The README keeps the initialized `VidSC` name and the imported project content.
Other depot projects and their history are excluded.

Extraction changes commit IDs because the project becomes the repository root. Older validation
records retain the original depot IDs; the table below maps them to the standalone history.
The initialization and import merge commits are additional standalone commits.

## Local artifacts and validation

The copied, ignored artifacts include the existing Release bundle, native downloads, current
relocatable scene runtime/wheels, fixtures, presentation evidence and benchmark reports.
Experiments, obsolete venvs, staging and trash remain in the depot archive. Real source clips
remain at their original read-only paths; they are not included in Git.

Historical reports retain original absolute paths and do not claim a new acceptance run.
Fixture input manifests in the standalone copy are relocated with their original JSON retained
alongside them. The copied bundle is the previously verified build, not a new native validation.
Artifact-copy checks and commit mapping are under ignored `artifacts/migration/`.

`tools/Test-CI.ps1` passes in the target repository: Release compilation, 241 unit tests, four
promotion/rollback scenarios and PowerShell syntax. Hosted GitHub CI also passed: run 38606473676 on commit ee711e7, with all 241 tests and artifact upload successful. Manual B6 is versioned but has not run; full M1 and deferred host tests remain
open. No UI automation or real-media benchmarks were run during migration.

## Commit mapping

| Depot | Standalone | Subject |
| --- | --- | --- |
| 7406e37 | 4c8c8e2 | Add standalone Windows CI and track manual delivery checks |
| 660ee21 | 30da303 | Defer remaining M1 host benchmarks until user resumes testing |
| 4971743 | 421537d | Record manual playback acceptance and timeline orientation TODO |
| db59bb2 | e35299f | Explain video timing and playback lessons from M1 |
| dd0ac08 | f970691 | Fix preroll seeks and qualify read-only presentation observations |
| 19680f8 | b9c51ee | Add M1 presentation acceptance and owned playback benchmarks |
| 6997d5f | 2bfb68e | Integrate pinned Flyleaf raw PTS and DXGI presentation evidence |
| 1e402fa | f3c7499 | Resolve vidsc frame boundaries from exact source PTS maps |
| 5201079 | d046d39 | Record vidsc navigation validation and M1 continuation state |
| 2fc6302 | 0ae949b | Add vidsc M1 measurement and presentation validation tools |
| e89e365 | 27e0a25 | Add zoomable vidsc timeline and keyboard navigation |
| 880fdcd | 5ead8b0 | Add bounded PTS-based vidsc navigation indexing |
| 152eb2a | 9697dbf | Harden vidsc presentation identity and freshness contracts |
| d5d1e29 | 2e2b172 | Fix vidsc dark theme text and control contrast |
| e725ace | 7c3346b | Implement vidsc M0 runtime bundle and minimal M1 playback prototype |
| 6068f0e | c24271e | Configure vidsc OpenCode supervision and unlimited task timeouts |
| a5cb5f0 | f665bba | added experimental skill: opencode-delegate |
| 0e10a31 | fab3c93 | docs(vidsc): add project agent guidance |
| 0523cb3 | 4f6ed6b | docs(vidsc): add video research and reviewed implementation plan |
