# Video Part Joining

Created: 2026-09-10
Updated: 2026-09-10

## Contract

`GET /hardware` advertises `videoPartJoining: true`. `POST /jobs/join` accepts
exactly two ordered `inputs`, each with `mountLabel` and `path`, a Matroska
`outputPath`, optional `outputMountLabel` and a nonempty UUID `clientJobId`.
Mounted path resolution rejects escapes; symbolic links, missing files, duplicate
paths and an output equal to an input are refused. Joining is separate from
`additionalInputs`, which adds sidecar tracks to an existing timeline.

The client id becomes the engine job id. Repeating a request with the same id and
paths returns the original job; changing its inputs or output under that id is
refused. Admission checks competing writers, including ordinary conversion jobs.
A file already present at the destination is not replaced. The engine also validates
join paths, Matroska output and N-format UUID ids for direct callers. External probes
run outside the admission gate; duplicate ids and output conflicts are checked again
before insertion. A rejected queue admission removes its journal so a retry can enqueue.

## Media handling

Both parts receive a fresh ffprobe inspection including codec-configuration hashes,
stream dispositions, language/title tags, timing and chapters. Corresponding tracks
must match by position. The supported set is SDR H.264/HEVC/MPEG-4 Part 2/MPEG-2
video in 8-bit YUV formats; AAC/AC-3/MP3/FLAC/16-bit PCM audio; SubRip, ASS/SSA and
WebVTT subtitles; and TrueType/OpenType attachments. The checks deliberately refuse
unverified or mismatched configurations instead of dropping tracks or re-encoding.
HDR, Dolby Vision, high-bit-depth video, object-based audio and bitmap subtitles
are not accepted.

The concat demuxer reads an escaped, engine-generated file list with explicit part
durations. For a Matroska part with a positive start offset, the effective duration
is its reported end timestamp minus that offset. Other containers with a positive
nonzero start are refused. Chapters receive a separate ffmetadata input, with the
second part shifted by the first part's duration. Matching font attachments are
copied. No external sidecar files are implicitly added.

The job copies streams to a hidden temporary Matroska file beside the destination,
reports `effectiveHardware: "none"` and progress against the sum of both durations, and checks that neither input
changed after inspection. Before publishing, the output is probed for its duration,
track count/codecs/tags and chapter count. Publication refuses to overwrite an
existing destination, even if a file appeared after admission. Failure and
cancellation remove temporary output and the temporary concat/metadata scripts.

## Restart behavior

Join metadata is journaled atomically under `AppDataDir/join-jobs`. Completed and
cancelled/failed states survive engine restarts; an interrupted queued/running join
is reported Failed with an explicit restart message, and its temporary files are
removed. It is not silently restarted. The original files and any already published
output are retained. Journal records also preserve idempotency after in-memory
job eviction; explicitly removing a job removes its journal.

## Testing Expectations

- `JoinEndpointTests`: resolved input order, stable client id, missing/duplicate
  inputs, path escape, output/input collision and non-Matroska output refusal.
- `VideoPartJoinTests`: stream configuration/language/timing mismatch, unsupported
  codecs, filename escaping and line-break rejection; real two-part ffmpeg joining
  in both orders, frame and decoded audio equivalence, seeking across the boundary,
  shifted subtitles/chapters, normalized nonzero Matroska timestamps and attachment
  byte preservation; existing output and competing writer refusal, queued cancel,
  completed-state restoration and interrupted-output cleanup; direct-call validation,
  concurrent retries, slow-probe admission isolation, queue rejection recovery,
  no-encoder reporting and cancellation without failure events.
- Set `FFMPEG_PATH` and `FFPROBE_PATH` for real fixture tests. These are explicitly
  skipped when those binaries are not supplied; ordinary unit tests need no tools.
