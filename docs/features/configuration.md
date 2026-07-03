# Configuration

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

All configuration is environment-driven and resolved once at startup — the app
hard-codes no ports or paths. `TranscodeEngineSettings.FromConfiguration`
(`Transcoding/TranscodeEngineSettings.cs`) reads the engine knobs; `Program.cs` reads
the runtime/port knobs. Values come from Hosty settings (the manifest), Hosty-injected
platform variables, and, in docker, `ASPNETCORE_URLS`.

## Hosty-injected platform environment

Set by Core, not by the operator:

| Variable | Read by | Purpose |
| --- | --- | --- |
| `HOSTY_APP_DATA_DIR` | engine | App data / scratch dir; the standalone fallback media root lives under `media/`. Falls back to `{contentRoot}/data` when unset. |
| `HOSTY_MOUNT_MEDIA` | engine | Comma-joined `label=path` media mounts, parsed into the label→root map. See [Media mounts](media-mounts.md). |
| `HOSTY_PORT_CONTROL` | Program.cs | Loopback control port under the `local` (localCommand) runtime; the app binds exactly this. Ignored in the container. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (+ other `OTEL_*`) | engine | Presence switches on OTLP export; absence = no telemetry. See [Hosty runtime app](hosty-runtime-app.md#telemetry). |
| `DOTNET_RUNNING_IN_CONTAINER` | Program.cs | Set by the docker image; when `true`, Kestrel's default binding (`ASPNETCORE_URLS`) is used instead of `HOSTY_PORT_CONTROL`. |
| `ASPNETCORE_URLS` | container | Container listen URL (`http://+:8080`), set by the image. |

## Operator settings (manifest)

Defaults come from `manifest.json`; the operator sets them through the Hosty Shell.

| Variable | Default | Meaning |
| --- | --- | --- |
| `HWACCEL` | `auto` | Default hardware acceleration when a job doesn't request one. `auto` picks VideoToolbox (native macOS), AMF (native Windows + AMD), VAAPI (Linux render device present), else software. Explicit: `vaapi`, `videotoolbox` (aliases `vt`/`macos`), `amf` (alias `windows`), `none` (aliases `software`/`cpu`). |
| `VAAPI_DEVICE` | `/dev/dri/renderD128` | Render node passed to ffmpeg's `-vaapi_device` and used by the hardware probe. |
| `MAX_CONCURRENT_JOBS` | `1` | Worker count (how many jobs run at once). Floored at 1 — hardware encoders have limited sessions. |

A per-job `hardwareAcceleration` on `POST /jobs` overrides the `HWACCEL` default; both
resolve through the same rules and fall back to software when the host can't satisfy
the choice.

## Engine-only / advanced variables

Read by `TranscodeEngineSettings` but not surfaced as manifest settings — set them
directly (e.g. for a local run) when needed:

| Variable | Default | Meaning |
| --- | --- | --- |
| `FFMPEG_PATH` | `ffmpeg` | Path to the `ffmpeg` binary; a bare name resolves on `PATH`. Useful to point the native `local` runtime at a specific host build. |
| `FFPROBE_PATH` | `ffprobe` | Path to the `ffprobe` binary (used to probe input duration). |

## Precedence notes

- The control port under `local` is `HOSTY_PORT_CONTROL`; in the container it is
  `ASPNETCORE_URLS` (the container flag `DOTNET_RUNNING_IN_CONTAINER` is what selects
  between them).
- Per-job `hardwareAcceleration` overrides the `HWACCEL` engine default; `crf` /
  `maxHeight` / stream selections are per-job only (no engine-wide default).
- `MAX_CONCURRENT_JOBS` is floored at 1 (`max(1, value)`), so a `0` or negative value
  still yields one worker.
- Numeric/enum settings that fail to parse fall back to their defaults rather than
  erroring at startup (`HWACCEL` → `auto`, `MAX_CONCURRENT_JOBS` → 1).

## Testing Expectations

`TranscodeEngineSettingsTests` (xUnit) cover the resolution rules: the `HWACCEL` value
parsing (`ParseHardware`, incl. aliases and unknown/empty → null), and the media-mount
parsing (delegated to the cases in [Media mounts](media-mounts.md)).
