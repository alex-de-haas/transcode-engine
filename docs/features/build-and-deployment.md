# Build and Deployment

Status: Implemented
Created: 2026-07-03
Updated: 2026-07-03

## Description

The engine ships as a single-service container image built from a framework-dependent
.NET 10 app, published multi-arch to GHCR, and installed by Hosty Core from the
manifest — or run natively via the `local` runtime for host-native encoders. This doc
covers the Dockerfile, the container entrypoint, CI, image publishing, and running
under each runtime.

## The image (`Dockerfile`)

A two-stage build from the repo root:

- **Build stage** (`mcr.microsoft.com/dotnet/sdk:10.0`). Restores and
  `dotnet publish -c Release` the API into a framework-dependent app. (Unlike the
  sibling torrent-engine, this app is **not** Native AOT — it ships on the managed
  `aspnet` runtime, which shelling out to ffmpeg does not conflict with.)
- **Runtime stage** (`mcr.microsoft.com/dotnet/aspnet:10.0`). Installs `ffmpeg` and the
  VA-API userspace stack — `vainfo`, `libva2`, `libva-drm2`, `mesa-va-drivers`,
  `libdrm2`. `mesa-va-drivers` covers both Intel (iHD/i965) and AMD (radeonsi); the
  host kernel driver behind a passed-through `/dev/dri` device does the actual work.
  With no device present the engine still runs and falls back to software encoding. It
  copies the published app + `docker/entrypoint.sh`, sets
  `ASPNETCORE_URLS=http://+:8080`, exposes `8080`, and runs the entrypoint.

Hardware VAAPI needs a `/dev/dri` render node at runtime, granted through the
`docker-vaapi` [manifest profile](hosty-runtime-app.md#runtime-profiles).

## The entrypoint (`docker/entrypoint.sh`)

The entrypoint is a diagnostic wrapper, not a gate. It logs whether the `/dev/dri`
passthrough worked — lists the devices and runs a best-effort `vainfo` against
`VAAPI_DEVICE` — so a misconfigured passthrough is obvious in the app logs. It **never
fails the start**: with no device it says so and continues, and the engine uses
software encoding. It then `exec`s `dotnet TranscodeEngine.Api.dll` so the API becomes
PID 1 and receives signals for a clean shutdown.

## CI (`.github/workflows/ci.yml`)

On pushes to `main` and on pull requests: restore, `dotnet build --configuration
Release`, and `dotnet test` the `TranscodeEngine.Api.Tests` project on `ubuntu-latest`
with the .NET 10 SDK. Superseded runs on the same ref are cancelled.

## Publishing (`.github/workflows/publish.yml`)

On pushes to `main` (→ `:latest`), version tags `v*` (→ `:vX.Y.Z`), and manual
dispatch, the image is built **multi-arch the fast way**: `linux/amd64` and
`linux/arm64` each build on their **own native runner** in parallel (no QEMU — the
emulated arm64 build is slow and flaky), push blobs by digest, and a final `merge` job
assembles the tagged manifest list from those digests. Images land at
`ghcr.io/alex-de-haas/transcode-engine`. Tags come from `docker/metadata-action`
(`latest` on the default branch, the git tag, and a short-SHA tag).

## Running under each runtime

Point Hosty Core at the manifest; the `media` mount and settings are configured through
the Shell. Which runtime you pick determines the available encoder (see
[Hardware acceleration](hardware-acceleration.md)):

```bash
# Default: software encoding, starts everywhere (incl. macOS Docker Desktop, no /dev/dri).
hosty apps install . --runtime docker

# Hardware VAAPI: only on a Linux host that exposes a render node (verify `ls -la /dev/dri` first).
hosty apps install . --runtime docker-vaapi
```

**VideoToolbox (native macOS)** and **AMF (native Windows + AMD)** need the `local`
runtime, which runs `dotnet run` on the host so `ffmpeg` is the host's own:

```bash
# macOS: host needs ffmpeg (with VideoToolbox) + the .NET SDK.
brew install ffmpeg dotnet
hosty apps install . --runtime local
hosty apps start com.haas.transcode-engine
```

On Windows the `local` runtime reaches the AMD **AMF** stack (hardware decode via
D3D11VA + the `*_amf` encoders) when the host has the AMD Adrenalin driver (which ships
`amfrt64.dll`), an ffmpeg build with AMF + d3d11va on `PATH`, and the .NET SDK.
`HWACCEL=auto` then picks the platform encoder automatically; force one with
`HWACCEL=videotoolbox` / `HWACCEL=amf` / `HWACCEL=vaapi`.

Before it is functional, bind at least one host path into the `media` mount with the
same label the consumer uses for its matching catalog root (see
[Media mounts](media-mounts.md)).

## Local development

The app runs directly for API/engine work without Hosty:

```bash
# from the repository root
dotnet test src/TranscodeEngine.Api.Tests/TranscodeEngine.Api.Tests.csproj
dotnet run --project src/TranscodeEngine.Api            # http://localhost:5xxx or ASPNETCORE_URLS
```

With no mount injected the engine uses a single unlabeled fallback media root at
`{contentRoot}/data/media`, and `HWACCEL=auto` resolves to the host's native encoder
(VideoToolbox on a Mac dev box) or software. This is enough to exercise the control
API, argument construction, and mount-label logic; hardware VAAPI requires the
`docker-vaapi` runtime on a Linux host with a render node.

## Testing Expectations

CI runs the xUnit suite on every push/PR. The image build is exercised by the `publish`
workflow. Hardware encoding (VAAPI / VideoToolbox / AMF) depends on real host devices
and is validated at the runtime level, not in CI — see
[Hardware acceleration](hardware-acceleration.md).
