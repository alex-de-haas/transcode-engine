# Hosty Runtime App

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

Transcode Engine is packaged as a [Hosty](https://github.com/alex-de-haas/docker-host)
runtime app (`manifest.json`, `schemaVersion: "app.0.1"`, id
`com.haas.transcode-engine`). Hosty Core owns its lifecycle — install, start/stop,
update, backup/restore, logs — and injects the environment the app reads (data dir,
mounts, ports, and, when enabled, telemetry). The app never hard-codes ports, origins,
or paths. This doc is the reference for the manifest and the platform contract; the
environment variables it produces are enumerated in [Configuration](configuration.md).

## Manifest anatomy

A single `engine` service with **three** runtime profiles (`defaultRuntime: docker`):

| Manifest section | Value / purpose |
| --- | --- |
| `runtimeProfiles` | `docker` (default), `docker-vaapi`, `local` (see below). |
| `services[].engine.runtimes.docker` | `ghcr.io/alex-de-haas/transcode-engine:latest`, `pullPolicy: always`, `control` → container port `8080` (http). **No device** — always starts. |
| `…runtimes.docker-vaapi` | Same image + port, plus `devices: ["/dev/dri"]` for hardware VAAPI. |
| `…runtimes.local` | `localCommand`: `dotnet run --project src/TranscodeEngine.Api --configuration Release` in the repo root, `control` port `8080`. Runs natively on the host for VideoToolbox / AMF. |
| `endpoints` | `control` → the `engine` service's `control` port; the consumer-facing HTTP surface. |
| `data` | Enabled; `/app/data` (docker) / the app data dir (local) is exposed as `HOSTY_APP_DATA_DIR` and covered by backup/restore. |
| `externalMounts.media` | `host-path`, `multiple`, `rw`, `required` — one host path per catalog filesystem (see [Media mounts](media-mounts.md)). |
| `settings` | `HWACCEL`, `VAAPI_DEVICE`, `MAX_CONCURRENT_JOBS` (see below). |
| `telemetry` | `{ enabled: true, sampleRatio: 0.1 }` — opt-in observability (see [Telemetry](#telemetry)). |
| `capabilities` | `update`, `restart`, `stop`, `remove`, `backup`, `restore`, `logs`. |

## Runtime profiles

The three profiles exist because the reachable encoder depends on where and how the
engine runs (see [Hardware acceleration](hardware-acceleration.md)):

- **`docker` (default)** — the published image, software encoding, **no** device. It
  starts on any host, including macOS Docker Desktop (which has no `/dev/dri`). This
  is the safe default: it never fails to start.
- **`docker-vaapi`** — the same image plus a `/dev/dri` device passthrough for
  hardware VAAPI on a Linux host that exposes a render node. It is a **separate,
  opt-in** profile precisely because a Docker `--device /dev/dri` is a hard
  requirement: if the host has no render node the daemon refuses to create the
  container (`no such file or directory`), so the engine's own software fallback would
  never get to run. Keeping the device off the default profile is what preserves
  "always starts".
- **`local`** — a `localCommand` profile that runs `dotnet run` on the host instead of
  in a container, so `ffmpeg` is the host's own. This is the only way to reach
  VideoToolbox (macOS) or AMF (Windows), since Docker there runs a Linux VM with no
  access to the platform frameworks or GPU.

### The `/dev/dri` device grant

`docker-vaapi` declares `devices: ["/dev/dri"]`. This is a **privileged** passthrough,
so it is declared explicitly in the manifest and surfaced at install review rather
than assumed, and it depends on Hosty Core's devices support. Choosing the profile is
how an operator opts a host into hardware VAAPI.

### Settings

All settings are Hosty settings, read from the environment at startup:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `HWACCEL` | string | `auto` | Default hardware acceleration when a job doesn't request one: `auto` / `vaapi` / `videotoolbox` / `amf` / `none`. |
| `VAAPI_DEVICE` | string | `/dev/dri/renderD128` | Render node passed to ffmpeg's `-vaapi_device` and used by the probe. |
| `MAX_CONCURRENT_JOBS` | number | `1` | How many jobs run at once. Defaults to 1 — hardware encoders have limited sessions. Floored at 1. |

## App data and backups

`HOSTY_APP_DATA_DIR` (`/app/data` in the container) is the app's scratch/state dir; it
is in the manifest's `data` targets, so Core's `backup`/`restore` cover it. The engine
holds **no** durable state there today (jobs are in-memory and not resumed across a
restart), but the directory is created on start and is where the standalone fallback
media root lives (`{HOSTY_APP_DATA_DIR}/media`) when no mount is injected. Transcode
**inputs and outputs** do not live here — they live on the `media` mounts (see
[Media mounts](media-mounts.md)).

## Endpoints and discovery

The app publishes one endpoint, `control`, over HTTP. A consumer declares this app as
a cross-app dependency and is handed the resolved base URL as an environment variable
(`HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`); it points its HTTP client there and never
hard-codes an address. See [Consumer integration](consumer-integration.md) for the
full wiring, including the current non-public-endpoint caveat.

Under the `local` runtime Core assigns a loopback port and injects it as
`HOSTY_PORT_CONTROL`; `Program.cs` binds exactly that (overriding any inherited
`ASPNETCORE_URLS`/`PORT`). Under docker the image sets
`DOTNET_RUNNING_IN_CONTAINER=true` + `ASPNETCORE_URLS=http://+:8080`, so Kestrel's
default binding is left alone.

## Telemetry

The engine instruments itself with OpenTelemetry — ASP.NET Core / `HttpClient` /
.NET-runtime traces and metrics, plus `ILogger` logs — exported over OTLP/HTTP. Export
is **entirely driven by the `OTEL_*` environment Hosty Core injects**
(`src/TranscodeEngine.Api/Telemetry/HostyTelemetry.cs`, wired in `Program.cs` via
`AddHostyTelemetry()`): when the operator has enabled observability and the collector
is running (docker runtime), traces/metrics/logs flow to it; otherwise — including any
`local` (localCommand) run — the endpoint is absent and the app emits nothing (the SDK
never falls back to `localhost:4318`). Opt-in is the `telemetry` block in the manifest
(`enabled: true`, `sampleRatio: 0.1`). The platform-side observability contract is
documented in the Hosty Core repo (`docs/features/observability.md` there), not this
one.

## Testing Expectations

Manifest/platform integration (the device passthrough, mount injection, endpoint
discovery, port binding per runtime, backups) is validated through Core-managed
runtime, not unit tests. The settings-resolution layer that reads this environment is
unit-tested — see [Configuration](configuration.md) and [Media mounts](media-mounts.md).
