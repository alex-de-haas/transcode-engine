# Transcode Engine

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-20

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
starts `max(1, MAX_CONCURRENT_JOBS)` worker tasks, each draining a shared **bounded**
`Channel<string>` of job ids (capped at 1024 pending, so a flood of `POST /jobs` can't
grow the queue and the job dictionary without limit). `MAX_CONCURRENT_JOBS` defaults to
**1** because hardware encoders have a limited number of concurrent sessions; raise it
only when the host (and encoder) can take the parallelism.

- **`CreateAsync`** probes the input duration with ffprobe (best-effort — a failure or
  a probe that exceeds the 30s timeout just means byte-only progress), reads the input
  file size, mints a GUID job id, stores the `TranscodeJob`, and writes the id to the
  queue. The job runs as soon as a worker is free. If the queue is full or the engine
  is shutting down the write fails and the job is removed with an error.
- **Worker loop** dequeues an id, skips it if the job is gone or already cancelled,
  and otherwise runs it to completion before taking the next — so each worker
  processes one job at a time.
- **Maintenance sweep** — a background loop evicts aged-out terminal jobs on a ~60s
  timer (see [Retention](#lifecycle-and-operations)), running alongside the workers.
- **`StopAsync`** completes the queue, cancels the worker token, kills every running
  ffmpeg, and waits up to 10s for the workers (and the sweep) to drain (best-effort).

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

**Retention.** A terminal (completed/failed/cancelled) job is kept so a late
`GET /jobs` poll still sees it, then evicted once it ages past ~1h (or when more than
500 terminal jobs are retained, oldest first) — so the dictionary, and the SSE snapshot
list, stay bounded no matter how many jobs have run. Eviction runs both after each job
finishes and on a ~60s background sweep, so even a job cancelled while still queued
(which never runs through the worker) ages out when the engine is otherwise idle. A
consumer that needs a permanent record persists the transition events itself (see
[Consumer integration](consumer-integration.md#driving-off-remote-events)).

## Running a job

`RunJobAsync` is the heart of the worker:

1. **Re-check cancel.** A job cancelled between dequeue and here is marked `Cancelled`
   without spawning ffmpeg.
2. **Resolve hardware** (`ResolveHardware`, see
   [Hardware acceleration](hardware-acceleration.md)), mark the job `Running`, raise
   `JobStarted`, and log the actually-selected encoder (e.g.
   `Job …: encoding with hevc_vaapi (vaapi)`).
3. **Spawn ffmpeg** with the built argument list, redirecting stdout (the `-progress`
   stream) and stderr (a rolling 20-line tail kept for diagnostics). ffmpeg writes to
   a **hidden temp file beside the destination** (`.{name}.{jobId}.part{ext}`, the
   extension preserved so ffmpeg still infers the muxer), never straight to the real
   output. The output directory is created first.
4. **Honour a late cancel.** A cancel that arrived while the process was starting
   (before it was attached) is applied once the process is attached.
5. **Wait for exit**, guarded by a no-progress **watchdog**: the wait is cancelled (and
   ffmpeg killed) if no `-progress` line arrives for 5 minutes, so a hung device init,
   a stalled read, or a special file that never returns can't block the worker — and,
   at the default `MAX_CONCURRENT_JOBS=1`, the whole engine — forever. On exit: a
   worker-token cancellation (shutdown) marks the job `Cancelled` (a normal shutdown is
   not a job failure); a watchdog kill → `Failed` + `JobFailed`; a set `CancelRequested`
   → `Cancelled`; exit code `0` → the temp file is atomically **renamed onto the real
   output** (replacing any existing file) → `Completed` + `JobCompleted`; any other exit
   → `Failed` + `JobFailed` (logged with the stderr tail). A blocking `WaitForExit()`
   after the async wait drains the output callbacks so that tail is complete.
6. **Clean up.** The process reference is dropped (so a later cancel/shutdown kill is a
   no-op, never touching a disposed object), and a **cancelled or failed** job's temp
   file is deleted. Because the encode only ever writes the temp file, a failed,
   cancelled, or interrupted job **never touches a pre-existing file at the output
   path** — the destination is replaced only by a rename on success.

`Dispose` is made idempotent with an `Interlocked.Exchange` on the CTS, because the
one instance is registered three ways and the DI container can dispose it more than
once.

## ffmpeg argument construction

`BuildArguments` is pure (no process spawn) and unit-tested directly. It builds the
argument list as:

- **Base:** `-hide_banner -nostdin -y`.
- **Hardware decode setup** (only when re-encoding — a video copy never touches the
  GPU): VAAPI adds `-vaapi_device <device>`; AMF adds `-hwaccel d3d11va
  -hwaccel_output_format d3d11` (hardware-decode on the AMD VCN, keeping the surfaces
  on the GPU — the filter chain then downloads them with `hwdownload` so the `*_amf`
  encoders get the system-memory frames they accept). The download format is
  deliberately *not* pinned at the decoder: that transfer cannot convert, so pinning
  `nv12` failed with `-22` on every 10-bit (P010) source.
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
  then the temp output path (renamed onto the real output on a clean exit — see
  [Running a job](#running-a-job)).

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
