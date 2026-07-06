# Control API

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-06

## Description

The control API is the consumer-facing surface of the engine: an ASP.NET Core
Minimal API (`src/TranscodeEngine.Api/Api/TranscodeEndpoints.cs`, plus `/healthz` and
`/hardware` in `Program.cs`) over HTTP, with a Server-Sent Events stream for live
progress and state transitions. It is the only way in — the ffmpeg engine is never
reached directly. Engine records (`JobDescriptor`, `JobSnapshot`, `HardwareStatus`)
are returned on the wire as-is; there is no separate DTO layer.

The API is stateless per request and has no auth today (the endpoint is non-public;
see [Consumer integration](consumer-integration.md)). JSON is serialized with
`System.Text.Json` web defaults.

## Endpoints

| Method &amp; path | Purpose | Success | Notable errors |
| --- | --- | --- | --- |
| `POST /jobs` | Create a transcode job | `200` `JobDescriptor` | `400` bad request/path/input |
| `GET /jobs` | List all live snapshots | `200` `JobSnapshot[]` | — |
| `GET /jobs/{jobId}` | One live snapshot | `200` `JobSnapshot` | `404` unknown id |
| `POST /jobs/{jobId}/cancel` | Cancel a running/queued job | `204` | — |
| `DELETE /jobs/{jobId}?deleteOutput=` | Forget a job (optionally delete its output) | `204` | — |
| `GET /events` | SSE stream (all jobs) | `200` `text/event-stream` | — |
| `GET /hardware` | Detected accelerators | `200` `HardwareStatus` | — |
| `GET /healthz` | Liveness | `200` `{ "status": "ok" }` | — |

`jobId` is a server-assigned GUID (32 hex chars). The cancel/remove handlers are
idempotent: an unknown id is a `204` no-op, not a `404`.

## `POST /jobs`

Body (`CreateJobRequest`). `inputPath` and `outputPath` are required; everything
else falls back to an engine default:

| Field | Type | Notes |
| --- | --- | --- |
| `inputPath` | string | **Required.** Path relative to the selected media mount (or absolute inside it). Must exist. |
| `outputPath` | string | **Required.** Where the result is written, relative to the (output) mount. Must differ from the resolved input. |
| `inputMountLabel` | string? | Selects the media mount the input resolves against. Required when several mounts are configured; optional with exactly one. See [Media mounts](media-mounts.md). |
| `outputMountLabel` | string? | Media mount for the output. Defaults to `inputMountLabel` when omitted. |
| `videoCodec` | string? | `h264`, `hevc` (default), or `copy` (remux the video untouched). Aliases: `h265`/`x265` → hevc, `avc`/`x264` → h264. |
| `hardwareAcceleration` | string? | `auto` (default), `vaapi`, `videotoolbox`, `amf`, or `none`. A choice the host can't satisfy falls back to software. See [Hardware acceleration](hardware-acceleration.md). |
| `crf` | int? | Software-encoder quality, `0`–`51`. Ignored by the hardware encoders. |
| `maxHeight` | int? | Downscale to this height (aspect kept, never upscales), `16`–`4320`. Omit to keep the source resolution. |
| `audioStreamIndexes` | int[]? | Absolute input stream indices to keep, in output order. Omit to copy **all** audio. |
| `subtitleStreamIndexes` | int[]? | Absolute input subtitle indices to keep. Omit to copy all. **Matroska (`.mkv`) output only.** |
| `defaultAudioStreamIndex` | int? | Mark one mapped audio track as the container default. Requires `audioStreamIndexes` and must be a member of it. |
| `defaultSubtitleStreamIndex` | int? | Same, for subtitles. Requires `subtitleStreamIndexes`; `.mkv` output only. |

The handler validates before it mutates: it parses the codec/hardware, range-checks
`crf` / `maxHeight`, rejects negative stream indices, rejects encode-only knobs
(`maxHeight` / `crf`) combined with `videoCodec: copy` as contradictory, requires a
chosen default track to be in its explicit index list, and rejects a subtitle
selection for a non-`.mkv` output (subtitles ride only in Matroska — see
[Transcode engine](transcode-engine.md#ffmpeg-argument-construction)). It then
resolves the input/output paths against the media mounts (an unknown `mountLabel` or
an off-mount path is a `400`), checks the input exists, and checks output ≠ input —
only then does it hand off to the engine, which probes duration and enqueues.

The response is a `JobDescriptor` — what is known immediately, before a worker picks
the job up:

| Field | Type | Notes |
| --- | --- | --- |
| `jobId` | string | Server-assigned GUID. |
| `inputPath` | string | The resolved absolute input path. |
| `outputPath` | string | The resolved absolute output path. |
| `durationSeconds` | double? | Input duration from ffprobe; `null` if the probe failed (progress is then byte-only). |
| `inputSizeBytes` | long? | Input file size; `null` if unreadable. |

## The per-job snapshot

`JobSnapshot` is a **live, in-memory** view (never persisted) returned by
`GET /jobs`, `GET /jobs/{jobId}`, and carried on every SSE event.

| Field | Type | Meaning |
| --- | --- | --- |
| `jobId` | string | Server-assigned GUID. |
| `name` | string? | Output file name (`Path.GetFileName(outputPath)`). |
| `effectiveHardware` | string? | Encoder family actually selected after auto-detect/fallback: `vaapi`, `videotoolbox`, `amf`, or `software`. `null` while still queued. |
| `state` | string | `Queued`, `Running`, `Completed`, `Failed`, or `Cancelled`. |
| `complete` | bool | `true` once the job finishes successfully. |
| `percentComplete` | double | `0`–`100`, 2-dp. Derived from `out_time / duration`; `0` while queued and `100` on complete when the duration was unknown. |
| `fps` | double | ffmpeg's current encode FPS, 2-dp (`0` when not running). |
| `speed` | double | Encode speed multiple (e.g. `2.5` = 2.5× realtime), 3-dp (`0` when not running). |
| `outputSizeBytes` | long | Bytes written so far (ffmpeg `total_size`). |
| `etaSeconds` | double? | Seconds to completion at the current speed; `null` when not running, stalled (speed `0`), or the duration is unknown. |

`effectiveHardware` is the quickest confirmation that hardware encoding is really in
effect — a job that reports `vaapi` / `videotoolbox` / `amf` **and** completes
definitely used hardware, since ffmpeg errors out if it cannot initialise the device
rather than silently dropping to software. See
[Transcode engine](transcode-engine.md#snapshot-and-progress-derivation) for how
these are derived.

## SSE event stream (`GET /events`)

`GET /events` returns `text/event-stream` (with `Cache-Control: no-cache` and
`X-Accel-Buffering: no`) and streams until the client disconnects. On connect the
server immediately flushes a `: connected` comment so `EventSource` fires `open` (and
any proxy commits the response headers) even with no jobs running, and it emits a
`: keepalive` comment every 15s while idle so a proxy/browser idle-timeout can't drop a
live but quiet stream. Each data frame is a named event whose `data:` is a JSON
`TranscodeEvent`:

```
event: progress
data: {"type":"progress","jobId":"abc…","snapshot":{…}}
```

| Event | When | Payload |
| --- | --- | --- |
| `progress` | Every 1.5s, one frame per **non-terminal** job (completed/failed jobs are skipped — their terminal state already went out as `completed`/`errored`) | `snapshot` set |
| `started` | A job transitions queued → running | `snapshot` set |
| `completed` | A job finishes successfully | `snapshot` set |
| `errored` | A job fails (non-zero ffmpeg exit or an error) | `snapshot` set |

`TranscodeEvent` is `{ type, jobId, snapshot }`. There is no per-job "cancelled"
event — a cancel is observed as the job's `state` in the next `progress` tick (or a
`GET /jobs/{jobId}`).

The stream is served from an in-memory fan-out hub (`Realtime/TranscodeEventStream.cs`):
each subscriber gets its own **bounded** channel (256 events, `DropOldest`), so one
slow reader drops its own oldest frames instead of blocking the broadcaster or other
subscribers. Because `progress` re-broadcasts a full snapshot every tick, a dropped
frame is self-healing — the next tick carries current state. The periodic tick and
the engine-event forwarding are done by `Realtime/TranscodeProgressBroadcaster.cs`.
There is no server→client backfill or replay: a client that connects mid-encode gets
the next `progress` tick, and should seed initial state from `GET /jobs`.

## `GET /hardware`

Returns a best-effort `HardwareStatus` — informational, not a correctness gate — so
a consumer can surface what the host actually offers:

| Field | Type | Meaning |
| --- | --- | --- |
| `vaapiAvailable` | bool | A VAAPI render node is visible in the container (the `/dev/dri` passthrough worked). |
| `vaapiDevice` | string? | The render node that would be used (`VAAPI_DEVICE` if it exists, else the first discovered node). |
| `renderDevices` | string[] | All `/dev/dri/renderD*` nodes visible. |
| `videoToolboxAvailable` | bool | `true` only when the engine runs natively on macOS. |
| `amfAvailable` | bool | `true` only when the engine runs natively on Windows and the AMD driver's `amfrt64.dll` is present. |
| `checkedAt` | DateTimeOffset | When the probe ran. |

See [Hardware acceleration](hardware-acceleration.md) for how these map to the
runtime profiles.

## Error envelope

A `400` returns a body `{ "error": "<message>" }`. The message is human-readable and
safe to surface to an operator (e.g. an unknown `mountLabel` lists the configured
labels). A `404` (`GET /jobs/{jobId}` for an unknown id) has an **empty** body — there
is no error envelope to parse. `/healthz` returns `{ "status": "ok" }`.

## Testing Expectations

Backend tests use xUnit and Imposter; endpoint tests host `MapTranscodeEndpoints` on
an in-memory `TestServer` with a mocked `ITranscodeEngine` (`TranscodeJobEndpointTests`).
Required coverage:

- `POST /jobs`: unknown `mountLabel` → `400`, missing input → `400`, `copy` +
  `maxHeight` → `400`, a chosen default without / not in its index list → `400`,
  subtitle selection on a non-`.mkv` output → `400`, and a valid request → `200`
  with the descriptor.
- Path resolution (label selection, traversal safety) is covered in
  [Media mounts](media-mounts.md).
- The snapshot/argument derivations that back these responses are unit-tested in the
  engine — see [Transcode engine](transcode-engine.md).
