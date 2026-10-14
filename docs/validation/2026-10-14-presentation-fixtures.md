# Presentation identity fixtures

Run: `pwsh -NoProfile -File tools/New-PresentationFixtures.ps1 -RunId m1-oracle-04`.
Evidence: ignored `artifacts/tests/presentation/m1-oracle-04/manifest.json` and four MP4 files. The manifest includes every expected index, PTS, duration, presentation end and UI PTS. Packaged FFmpeg N-121120-gc74181a04b-20250918 and adjacent packaged ffprobe were used; no host CLI or playback-hook observations define expected identity.

All fixtures contain 48 frames at 320x180. Every frame visibly prints `FRAME n` and encodes n in twelve horizontal binary tiles, least significant bit first, at x=16, y=20, tile size 24x60. Arial is resolved explicitly from Windows Fonts. Generation fails if unavailable. Encoders use B frames; ffprobe reports `has_b_frames=2` for all four fixtures. AVC is yuv420p; HEVC is yuv420p10le. Packaged libx265 was available, so no fixture was skipped.

All oracle integers use time base 1/24000. Probe verifies this base rather than defining the expected map.

| Fixture | Raw presentation PTS formula, n=0..47 | Duration | First / last PTS / last end |
| --- | --- | --- | --- |
| cfr24 | 1000n | 1000 | 0 / 47000 / 48000 |
| fractional24000-1001 | 1001n | 1001 | 0 / 47047 / 48048 |
| irregular-offset | 120000 + 6000 floor(n/3) + [0,1000,3000][n mod 3] | [1000,2000,3000][n mod 3] | 120000 / 213000 / 216000 |
| hevc-main10 | 1001n | 1001 | 0 / 47047 / 48048 |

The irregular fixture starts at source epoch 120000 (five seconds); UI epoch is zero and UI PTS subtracts 120000. Other source/UI epochs are zero. Presentation intervals are half open. Interior VFR durations follow successive mathematical PTS; decoder frame-duration metadata can reflect reordered packet durations and does not define interior presentation ends. Final duration is explicitly verified against probe metadata.

Validation passed: exact mathematical PTS and 48-frame count for each fixture, final duration, full independent packaged FFmpeg gray decode, all twelve tile means of every decoded frame, sequential identities 0..47, and exact EOF byte count. Each fixture also rejects three deliberately damaged identity sequences: omitted final frame, duplicated final identity, and corrupted middle identity. This checks oracle rejection logic; the encoded source itself is not deliberately corrupted.

These fixtures establish a source oracle only. Rendered screenshot identity, actual hook event correspondence, seek/step/source-generation behavior and M1 playback acceptance remain separate checks. Raw decode buffers are bounded to 2,764,800 bytes per fixture and deleted after validation. No production build or long supplied source is needed.
