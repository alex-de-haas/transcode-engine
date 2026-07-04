# Consumer Integration

Status: Implemented (engine side); consumer wiring in progress
Created: 2026-07-03
Updated: 2026-07-03

## Description

The engine exists to be driven by another Hosty app. This doc describes how a consumer
wires it as a cross-app dependency, discovers it at runtime, shares media mounts with
it, and tolerates its absence. [Media Server](https://github.com/alex-de-haas/media-server)
is the intended reference consumer: it ships a transcode client for this app's control
API, and the remaining work is a consumer-side job/UI surface to actually request
transcodes (see [root roadmap](../root.md#roadmap)). The engine-side contract below is
complete.

## Declaring the dependency

The consumer wires the engine by its `control` endpoint in its own manifest:

```json
"dependencies": [
  {
    "id": "com.haas.transcode-engine",
    "required": false,
    "endpoints": [{ "key": "control", "as": "transcode-engine" }]
  }
]
```

`required: false` fits the intended posture — transcoding is an enhancement, not a
prerequisite for the consumer to serve its library.

## Discovery

Core injects the resolved base URL into the consumer, named after the endpoint alias —
`HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`. The consumer points its HTTP client at that
value; no address, port, or origin is hard-coded. The client then speaks the
[Control API](control-api.md): `POST /jobs` to create, `GET /jobs[/{jobId}]` to poll,
`POST /jobs/{jobId}/cancel` and `DELETE /jobs/{jobId}` to control, `GET /events` to
consume progress and transitions as they happen, and `GET /hardware` to surface what
the host can do.

## Sharing media mounts

For the finished output to be usable in place (no cross-container copy), the consumer
and engine must read and write on the same filesystem. The operator binds each of the
engine's `media` mounts to the **same host path and the same label** as the consumer's
matching catalog root; the consumer then sends that label as `inputMountLabel` (and,
if writing elsewhere, `outputMountLabel`) on `POST /jobs`. The engine resolves the
relative input/output paths against the root under that label, so the job lands on the
filesystem the consumer's library lives on. The label is the only key shared across
the two apps — Hosty configures each app's mounts independently. See
[Media mounts](media-mounts.md) for the full contract.

## Driving off remote events

A consumer creates a job and then tracks it off the SSE stream rather than polling: it
subscribes to `GET /events` and re-drives its own pipeline off `started` / `completed`
/ `errored` (and reads live progress from `progress`). Job progress is **not**
persisted by the engine — the consumer treats snapshots as ephemeral and persists only
the transitions it cares about. A consumer that connects mid-encode seeds current state
from `GET /jobs`, then follows the stream (there is no server-side replay).

Each snapshot's `effectiveHardware` lets the consumer confirm hardware encoding is in
effect (or surface that it fell back to software), and `etaSeconds` / `percentComplete`
drive a progress UI.

## Tolerating absence

A consumer should degrade gracefully when the engine is not present — keep the rest of
its surface working when `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL` is unset and simply
disable transcoding (e.g. behind a `Disabled…` fallback of the same client interface,
so the rest of the code is unaware). It can also gate readiness on `GET /healthz`
(liveness) before submitting jobs.

## Current caveat — non-public endpoint

The `control` endpoint is **non-public**, but Hosty cross-app `dependencies` today
resolve a *public*, host-reachable endpoint. Reaching a non-public endpoint across
containers needs the planned shared cross-app docker network, and real multi-tenant use
additionally needs the Hosty app-identity token mechanism for auth. Until those land,
this is a **trusted single-tenant** deployment with no auth on the control API. Do not
expose the control port publicly.

## Testing Expectations

The cross-app wiring (dependency resolution, the injected URL, mount-label sharing) is
validated at the Hosty runtime level on the consumer side, not by this app's unit
tests. On the engine side, the control API and mount-label contracts consumers rely on
are covered by [Control API](control-api.md) and [Media mounts](media-mounts.md) tests.
