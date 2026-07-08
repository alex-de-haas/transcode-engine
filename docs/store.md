![Transcode Engine](../assets/icon.svg)

# Transcode Engine

An **ffmpeg-backed batch transcoder** delivered as a Hosty runtime app. It exposes an
HTTP/SSE **control API** so other Hosty apps can drive re-encoding jobs over a
cross-app dependency, instead of shelling out to ffmpeg in-process.

The first intended consumer is **Media Server**. Running transcoding here — in its own
container — isolates two things:

- **Hardware encoding** — VAAPI needs a host `/dev/dri` render node passed into the
  container. Confining that privileged passthrough to one small, single-purpose app
  keeps it out of the consumer, which holds the database, tokens, and streaming surface.
- **Resources** — encoding is CPU/GPU-bound and long-running, so it gets its own
  container and never competes with the consumer's request-serving process.

## What it does

- **Hardware acceleration** — VAAPI (Linux `/dev/dri`), VideoToolbox (native macOS),
  AMF + D3D11VA (native Windows + AMD), or a software x264/x265 fallback. `auto` picks
  the best available; a job may request one explicitly.
- **Job queue + workers** — a bounded set of worker loops drain the queue and spawn one
  ffmpeg process per job, parsing its `-progress` stream into live snapshots.
- **Control API + SSE** — create, list, inspect, cancel, and remove jobs, plus
  `GET /events` streaming progress and started/completed/errored transitions.
- **Media mounts** — one labelled host path per catalog filesystem, selected per job by
  `mountLabel`, so a job reads and writes on the same filesystem as the consumer's
  catalog (zero-copy hand-off).

## Runtimes

Ships three runtime profiles: a default **`docker`** (software encoding, runs on any
host), an opt-in **`docker-vaapi`** that adds `/dev/dri` device passthrough for Linux
hosts with a render node, and a native **`local`** command for host-native encoders such
as VideoToolbox on macOS.

## Using it

Install from the marketplace and add it as a dependency from a consumer app (e.g. Media
Server). The control endpoint is non-public — it's reached over Hosty's intra-app
service discovery, not exposed to the browser.
