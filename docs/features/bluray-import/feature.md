# Blu-ray Inspection and MKV Jobs

Created: 2026-09-25
Updated: 2026-09-25

## Disc inspection

`POST /bluray/inspect` reads mounted BDMV playlists, durations, chapter counts and
clip sequences. Selecting a playlist adds MKVToolNix track identifiers and probed
picture information. Inspection rejects missing members, symbolic links and
unsupported multi-angle playlists. A revision binds later jobs to the inspected
disc inventory. Menus and decryption are outside this workflow.

`GET /hardware` reports `tools.blurayImport` when MKVToolNix 81 or newer is
available. Native and dev installations resolve `mkvmerge` through PATH or
`MKVMERGE_PATH`; Homebrew installation uses `brew install mkvtoolnix`.

## MKV creation

`POST /jobs/bluray` queues a durable job for one playlist, its primary video,
selected audio and optional subtitles. Track language, title, default and forced
flags are explicit. Streams are copied without re-encoding. The mux command keeps
short final MPLS chapters that MKVToolNix otherwise drops by default.

Jobs use temporary files, check available space and refuse to overwrite an existing
output. Publication checks duration, chapter count, selected track formats and flags,
and the probed picture/HDR/Dolby Vision configuration. A mismatch fails the job and
removes its temporary output. Sources remain untouched. Header comparisons do not
establish preservation of every Dolby Vision RPU or enhancement-layer payload.

Stable client job IDs make retries idempotent. Journal records retain terminal
status across restarts; interrupted jobs become failed and require a new operation.

## Testing Expectations

- xUnit covers MPLS timing, chapters, track selection, output validation, path
  boundaries, changed revisions, capability reporting and restart behavior.
- Endpoint tests use Imposter and in-memory hosting.
- Real-disc acceptance includes 1080p, multi-clip, UHD/HDR, full Dolby Vision
  payload preservation, cancellation and output publication; outstanding work is
  recorded in the [plan](plan.md).
