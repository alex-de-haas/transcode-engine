# Transcode Engine

A standalone [Hosty](https://github.com/alex-de-haas/docker-host) runtime app: an **ffmpeg-backed batch
transcoder** that exposes an HTTP/SSE **control API** for other Hosty apps to drive re-encoding jobs over a
dependency.

The first intended consumer is **Media Server**. Running transcoding here (rather than in-process inside the
consumer) solves two problems:

1. **Hardware encoding isolation** — VAAPI hardware encoding needs a host `/dev/dri` render node passed into
   the container. Confining that privileged passthrough to one small, single-purpose app keeps it out of the
   consumer (which holds the database, tokens, and the Jellyfin surface).
2. **Resource isolation** — encoding is CPU/GPU-bound and long-running; it gets its own container so it never
   competes with the consumer's request-serving process and is not killed by the consumer's restart/backup.

This mirrors the sibling [`torrent-engine`](https://github.com/alex-de-haas/torrent-engine) app: a single
job/SSE control API, a shared host-path mount for zero-copy file hand-off, and a consumer that drives it as a
cross-app dependency.

## Status

Implemented:
- **App manifest** (`manifest.json`) — a `docker` service with a `/dev/dri` device passthrough (requires
  docker-host capabilities/devices support) plus a `local` (native) runtime profile for host-native encoders
  (macOS VideoToolbox), a shared `media` mount, and the control port.
- **Container** (`Dockerfile` + `docker/entrypoint.sh`) — ffmpeg + the VA-API userspace stack
  (`mesa-va-drivers` covers Intel iHD/i965 and AMD radeonsi). The entrypoint logs the visible `/dev/dri`
  devices and a best-effort `vainfo` so a misconfigured passthrough is obvious in the logs; it never fails
  the start (with no device, software encoding still works).
- **ffmpeg engine** (`src/TranscodeEngine.Api/Transcoding`) — a bounded set of worker loops drain a job
  queue, spawn ffmpeg per job (VAAPI software-decode → `hwupload` → hardware-encode; VideoToolbox on native
  macOS; or libx264/libx265), and parse ffmpeg's `-progress` stream into live snapshots.
- **Control API + SSE** (`src/TranscodeEngine.Api/Api`, `.../Realtime`) — create/list/inspect/cancel/remove
  jobs, and `GET /events` streaming progress + started/completed/errored transitions.
- **Multiple media mounts** — one labelled host path per catalog filesystem, selected per job by
  `mountLabel`, so a job reads and writes on the same filesystem as the consumer's catalog.

TODO (next chunks):
- Consumer wiring in Media Server beyond the engine client (a job/UI surface to actually request transcodes).
- Resolution scaling and audio re-encode options (today audio is `-c:a copy`).
- Secure cross-app calling — the control endpoint is non-public, so today this is a trusted single-tenant
  deployment with no auth (same posture as `torrent-engine`).
- NVENC / QSV beyond VAAPI (NVENC needs the NVIDIA Container Toolkit, a separate docker-host feature).

## Control API

```text
POST   /jobs              { inputMountLabel?, inputPath, outputMountLabel?, outputPath, videoCodec?, hardwareAcceleration?, crf? } -> descriptor
GET    /jobs
GET    /jobs/{jobId}
POST   /jobs/{jobId}/cancel
DELETE /jobs/{jobId}?deleteOutput=
GET    /events            (SSE: progress, started, completed, errored)
GET    /hardware          { vaapiAvailable, vaapiDevice, renderDevices, videoToolboxAvailable, checkedAt }
GET    /healthz
```

`inputPath` / `outputPath` are resolved against the `media` mount selected by their label. The `media`
external mount is `multiple`, so the operator binds one host path per catalog filesystem — each with the
**same label** the consumer uses for the matching catalog root (the only key shared across the two apps,
since Hosty configures each app's mounts independently). The label is required when more than one media mount
is configured and optional when there is exactly one (or for the standalone fallback root). An unknown label,
a missing input, or a path that escapes the root is a `400` so a job is never read or written off-mount.
`outputMountLabel` defaults to `inputMountLabel`.

- `videoCodec` — `h264` or `hevc` (default `hevc`).
- `hardwareAcceleration` — `auto` (VideoToolbox on a native macOS host, VAAPI when a Linux render device is
  present, else software), `vaapi`, `videotoolbox`, or `none` (default `auto`). A choice the host can't
  satisfy falls back to software.
- `crf` — software-encoder quality (0–51); ignored by the hardware encoders.

Each job snapshot (`GET /jobs`, `GET /jobs/{id}`, and the SSE `progress` events) carries `effectiveHardware`
— the encoder family actually selected after auto-detection/fallback (`vaapi` / `videotoolbox` / `software`),
so you can confirm hardware encoding is in effect. The engine also logs it per job, e.g.
`Job …: encoding with hevc_vaapi (vaapi)`. A `*_vaapi` / `*_videotoolbox` job that completes definitely used
hardware — ffmpeg errors out if it cannot initialise the device rather than silently dropping to software.

`GET /hardware` reports whether a VAAPI render node is visible inside the container (i.e. the `/dev/dri`
passthrough worked), which render devices were found, and whether VideoToolbox is reachable (only when the
engine runs natively on macOS — see [Hardware acceleration](#hardware-acceleration)).

## Hardware acceleration

| Encoder | Runtime | How |
| --- | --- | --- |
| **VAAPI** (Intel / AMD) | `docker` | A passed-through `/dev/dri` render node (manifest `devices`). The default production target. |
| **VideoToolbox** (Apple) | `local` (native) | The engine runs natively on the macOS host via the `localCommand` runtime; the host's `ffmpeg` reaches VideoToolbox directly. |
| **Software** (libx264 / libx265) | any | Fallback when no hardware is available. |

**Why VideoToolbox needs the native runtime.** Docker on macOS runs containers in a Linux VM with no access
to Apple frameworks or the GPU, so VideoToolbox (and any hardware encode) is unreachable from the `docker`
runtime on a Mac. To use it, run the engine through the **`local`** runtime profile, which executes
`dotnet run` on the host so `ffmpeg` is the host's own (e.g. Homebrew `ffmpeg`, which ships VideoToolbox):

```bash
brew install ffmpeg dotnet            # host needs ffmpeg (with VideoToolbox) + the .NET SDK
hosty apps install . --runtime local
hosty apps start com.haas.transcode-engine
```

`HWACCEL=auto` then picks VideoToolbox automatically on macOS; set `HWACCEL=videotoolbox` to force it. The
same `local` runtime is how you'd reach any other host-native encoder (e.g. NVENC) without docker device
plumbing.

## Consumer integration

A Hosty app drives this engine by declaring it as a cross-app dependency and calling the control API:

- **Dependency** (consumer manifest) — wire the engine by its `control` endpoint:
  `"dependencies": [{ "id": "com.haas.transcode-engine", "required": false, "endpoints": [{ "key": "control", "as": "transcode-engine" }] }]`.
- **Discovery** — Core injects the resolved base URL into the consumer as
  `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL` (named after the endpoint alias). The consumer points its HTTP
  client at that value; no address is hard-coded.
- **Media mounts** — the consumer binds each of this app's `media` mounts to the **same host path and the
  same label** as its matching catalog root, then sends that label as `mountLabel`. This keeps the job's read
  and write on one filesystem with the consumer's library (zero-copy).
- **Availability** — a consumer should tolerate the engine being absent: when
  `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL` is unset, it disables transcoding and keeps the rest of its surface
  working.

## Networking model

```
consumer app (bridge)  ──HTTP/SSE──►  control API (bridge, control port)
                                       │
                                       transcode-engine container
                                       │  ffmpeg + VAAPI (/dev/dri passthrough)
                                       └─ reads input, writes output
shared media volume (one filesystem, same paths/labels as the consumer's catalog roots)
```

## Open questions

- **Cross-app auth/routing:** the `control` endpoint is **non-public** while Hosty cross-app `dependencies`
  today resolve a *public*, host-reachable endpoint. Reaching a non-public endpoint across containers needs
  the planned shared cross-app docker network. Until then this assumes a trusted single-tenant deployment.
- **WSL2 device exposure:** VAAPI in docker on Windows 11 + WSL2 requires the host to expose `/dev/dri`
  (with an assigned render node) inside the WSL2 distro first. Verify `ls -la /dev/dri` and `vainfo` in the
  WSL2 shell before relying on the in-container `GET /hardware`.
