# Transcode Engine

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

`FfmpegTranscodeEngine` (`src/TranscodeEngine.Api/Transcoding/FfmpegTranscodeEngine.cs`)
is a thin wrapper over ffmpeg. It is registered once and serves three roles at the
same time: the `ITranscodeEngine` the control API resolves, the `IHostedService` that
starts/stops the worker pool with the app, and the source of the events the
broadcaster forwards onto SSE. It owns **no** persistence — jobs live in an in-memory
dictionary and are never resumed across a restart — and surfaces only live snapshots
plus start/complete/fail transition events.

The engine is configured entirely from `TranscodeEngineSettings` (see
[Configuration](configuration.md)); it never hard-codes ffmpeg paths, the render
device, or the worker count.

## Job queue and worker loops

On `StartAsync`, the engine creates its app-data directory and every media root, then
starts `max(1, MAX_CONCURRENT_JOBS)` worker tasks, each draining a shared unbounded
`Channel<string>` of job ids. `MAX_CONCURRENT_JOBS` defaults to **1** because
hardware encoders have a limited number of concurrent sessions; raise it only when
the host (and encoder) can take the parallelism.

- **`CreateAsync`** probes the input duration with ffprobe (best-effort — a failure
  just means byte-only progress), reads the input file size, mints a GUID job id,
  stores the `TranscodeJob`, and writes the id to the queue. The job runs as soon as
  a worker is free. If the engine is shutting down the write fails and the job is
  removed with an error.
- **Worker loop** dequeues an id, skips it if the job is gone or already cancelled,
  and otherwise runs it to completion before taking the next — so each worker
  processes one job at a time.
- **`StopAsync`** completes the queue, cancels the worker token, kills every running
  ffmpeg, and waits up to 10s for the workers to drain (best-effort).

## Lifecycle and operations

`ITranscodeEngine` (`Transcoding/ITranscodeEngine.cs`) is the full contract:

- **`CreateAsync(request, ct)`** — probe + enqueue, returns the `JobDescriptor`.
- **`CancelAsync(jobId, ct)`** — sets the job's `CancelRequested` flag and kills its
  ffmpeg process if one is running; a still-queued job is marked `Cancelled`
  immediately (the worker then skips it). A no-op for an unknown id.
- **`RemoveAsync(jobId, deleteOutput, ct)`** — forgets the job (removes it from the
  dictionary), cancels/kills it, and — when `deleteOutput` — deletes its (possibly
  partial) output file. A no-op for an unknown id.
- **`GetSnapshot` / `GetAllSnapshots`** — read-only live views built from each job's
  locked state.

State lives in a single `ConcurrentDictionary<string, TranscodeJob>` keyed by job id;
each `TranscodeJob` guards its own mutable fields with a lock so the worker thread and
the API/broadcaster threads always observe a consistent `ToSnapshot()`.

## Running a job

`RunJobAsync` is the heart of the worker:

1. **Re-check cancel.** A job cancelled between dequeue and here is marked `Cancelled`
   without spawning ffmpeg.
2. **Resolve hardware** (`ResolveHardware`, see
   [Hardware acceleration](hardware-acceleration.md)), mark the job `Running`, raise
   `JobStarted`, and log the actually-selected encoder (e.g.
   `Job …: encoding with hevc_vaapi (vaapi)`).
3. **Spawn ffmpeg** with the built argument list, redirecting stdout (the `-progress`
   stream) and stderr (a rolling 20-line tail kept for diagnostics). The output
   directory is created first.
4. **Honour a late cancel.** A cancel that arrived while the process was starting
   (before it was attached) is applied once the process is attached.
5. **Wait for exit.** A worker-token cancellation (shutdown) kills ffmpeg and marks
   the job `Cancelled` — a normal shutdown is not a job failure. Otherwise: a set
   `CancelRequested` → `Cancelled`; exit code `0` → `Completed` + `JobCompleted`;
   any other exit → `Failed` + `JobFailed` (logged with the stderr tail).
6. **Clean up.** The process reference is dropped (so a later cancel/shutdown kill is
   a no-op, never touching a disposed object), and a **cancelled or failed** job's
   partial output file is deleted so it doesn't linger on the catalog. A completed
   job's output is the good result and is kept.

`Dispose` is made idempotent with an `Interlocked.Exchange` on the CTS, because the
one instance is registered three ways and the DI container can dispose it more than
once.

## ffmpeg argument construction

`BuildArguments` is pure (no process spawn) and unit-tested directly. It builds the
argument list as:

- **Base:** `-hide_banner -nostdin -y`.
- **Hardware decode setup** (only when re-encoding — a video copy never touches the
  GPU): VAAPI adds `-vaapi_device <device>`; AMF adds `-hwaccel d3d11va
  -hwaccel_output_format nv12` (hardware-decode on the AMD VCN, then download the
  frames to system memory so the `*_amf` encoders accept them).
- **Input:** `-i <inputPath>`.
- **Stream maps:** the primary video only — `0:v:0`, never a bare `0:v` (which would
  also grab attached cover-art "video" streams the hardware encoders reject) — then
  the audio and (Matroska-only) subtitle streams. A `null` selection maps every
  stream of the type with the optional form (`0:a?` / `0:s?`, so a missing type
  doesn't fail the job); an explicit list maps those absolute indices in order. When
  subtitles are kept, attachment fonts (`0:t?`) are mapped too, so ASS subtitles still
  render.
- **Video encode:** `-c:v copy` for a remux, else the selected encoder plus an
  optional downscale (`AddVideoEncode`). See
  [Hardware acceleration](hardware-acceleration.md#encoder-families-and-the-encode-chain).
- **Audio/subtitle codecs:** `-c:a copy`; `-c:s copy` for Matroska. Audio is always
  copied today, never re-encoded.
- **Default-track disposition:** `-disposition:<kind>:<pos>` entries that make exactly
  one mapped track of a type the container default. This needs the explicit index
  list to translate the chosen absolute index into the output-relative position, and
  is a no-op for a copy-all selection or an out-of-set index (defence in depth — the
  endpoint already rejects one, but `AddDefaultDisposition` stays correct in
  isolation so a stray index never clears every default).
- **Progress:** `-progress pipe:1 -nostats` (structured key/value progress on stdout),
  then the output path.

### Why subtitles are Matroska-only

`mkv` carries any subtitle/attachment codec, so a stream **copy** always works. Other
containers (e.g. `mp4`) reject most subtitle codecs on copy, which would fail the
whole job — so subtitles and attachments are omitted for non-`.mkv` outputs, and the
endpoint rejects a subtitle *selection* against a non-`.mkv` output rather than
silently doing nothing.

## Snapshot and progress derivation

`TranscodeJob.ApplyProgressLine` folds one `key=value` line of ffmpeg's `-progress`
output into the live fields, and `ToSnapshot` computes the view (see the
[Control API snapshot table](control-api.md#the-per-job-snapshot) for field
semantics). The non-obvious parts:

- **`out_time_us` / `out_time_ms`** are both microseconds in ffmpeg (`out_time_ms` is
  historically mislabeled), so both map to `out_time` seconds.
- **`speed`** has its trailing `x` stripped (`2.5x` → `2.5`).
- **`percentComplete`** is `out_time / duration` (clamped 0–100); with an unknown
  duration it is `0` until the job completes, then `100`.
- **ETA** is `(duration − out_time) / speed`, but `null` unless the job is running
  with a known duration and a non-zero speed — so a consumer never renders a bogus
  countdown for a queued, stalled, or complete job.
- **On complete**, `out_time` is pinned to the full duration and speed/fps are zeroed,
  so a finished job reads a clean 100% at 0 fps rather than the last mid-encode tick.
- **`effectiveHardware`** reflects the encoder family resolved at `Start`; a
  still-queued job reports `null`.

## Testing Expectations

Backend tests use xUnit and Imposter. Required coverage (`FfmpegTranscodeEngineTests`,
exercising the pure `BuildArguments`):

- Primary video is mapped as `0:v:0` and audio is copied, not re-encoded.
- Matroska keeps all subtitles + attachments (`0:s?`, `0:t?`, `-c:s copy`);
  non-Matroska keeps audio but omits subtitle maps and `-c:s`.
- Explicit audio selection maps only those indices; an empty subtitle selection drops
  subtitles/attachments while leaving audio copied.
- `videoCodec: copy` remuxes with no encoder/scale/hwaccel even when hardware and a
  `maxHeight` were requested.
- `maxHeight` adds a CPU `scale=-2:H` for software and a GPU `scale_vaapi` for VAAPI.
- A chosen default track sets `-disposition` by output position; a default not in the
  selection leaves dispositions untouched.

Actual encoding (spawning ffmpeg, hardware init) depends on real host tooling and is
validated at the runtime level, not by unit tests.
