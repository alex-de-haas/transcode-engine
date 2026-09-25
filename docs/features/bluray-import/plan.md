# Blu-ray Inspection and MKV Jobs

Status: In Progress
Created: 2026-09-24
Updated: 2026-09-25

## Goal

Implement the engine deliverables approved in the Media Server
[Blu-ray plan](https://github.com/alex-de-haas/media-server/blob/main/docs/features/bluray-import/plan.md).
The user authorized implementation on 2026-09-24. This engine owns disc inspection,
playlist-to-MKV conversion, tool selection, durable jobs and validation.

## Target behavior

Mounted unencrypted BDMV directories expose playlist-specific tracks and revision
identities. Explicit jobs copy the primary picture and selected audio/subtitles
into a new MKV, retaining chapters and UHD/HDR/Dolby Vision data. MKVToolNix 81+
provides the Blu-ray dual-layer reader; capability discovery refuses older tools.
No menus, decryption, episode mapping or automatic conversion are introduced.
Outputs use temporary files and no-overwrite publication. Requests are idempotent;
restarts preserve terminal status and fail interrupted jobs without touching inputs.

## Deliverables

- [x] Inspection and capability contract, path and revision validation.
- [x] Selected-playlist MKV jobs with track metadata/default/forced controls.
- [x] Durable admission, progress, cancellation, restart and output validation.
- [x] Unit and endpoint coverage; build and test checks.
- [ ] Verify full-duration Dolby Vision RPU/enhancement-layer payloads in representative outputs, including transport-stream inputs without ffprobe configuration records. Header comparison alone is insufficient evidence of payload preservation.
- [ ] Real-disc 1080p, multi-clip, UHD and Dolby Vision acceptance, including layers.
- [x] Feature documentation, minor release version and generated index (0.11.0 → 0.12.0).

## Open questions

None blocking implementation. Representative discs are acceptance inputs to obtain
later, as explicitly agreed; required UHD/Dolby Vision scope is not optional.

## Verification

Run dotnet build and dotnet test on
`src/TranscodeEngine.Api.Tests/TranscodeEngine.Api.Tests.csproj`, documentation index
validation and git diff --check. Record real-disc results before claiming complete
UHD/Dolby Vision preservation. Keep incomplete acceptance deliverables unchecked.

The mux command explicitly preserves short final MPLS chapters. On the Maximka
playlist `00005`, default MKVToolNix identification reports 12 chapters while
`--engage keep_last_chapter_in_mpls` reports all 13 MPLS entry marks. This
read-only check does not replace a completed real-disc MKV acceptance run.

PR preparation on 2026-09-25: Release build and all 360 xUnit tests pass with
FFMPEG_PATH=/opt/homebrew/bin/ffmpeg and FFPROBE_PATH=/opt/homebrew/bin/ffprobe.
Documentation index validation and git diff --check pass. The PR remains draft
until the unchecked acceptance deliverables above are resolved.
