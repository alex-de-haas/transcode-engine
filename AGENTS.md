# Agent Instructions for Transcode Engine project

## Versioning

This app uses semantic versioning `major.minor.patch`, bumped per release. When a
change ships, bump the app version in the **same commit**:

- **patch** — bug fix or small enhancement to existing functionality.
- **minor** — new functionality, or a large/breaking change (while the app is in `0.x`).
- **major** — reserved until the app declares a stable `1.0.0`; after that, breaking
  changes for the app's users.

Where the version lives:

- `version` in `manifest.json` is the app's release version and the source of truth —
  bump it in the same change that ships the work.
- Do **not** bump `schemaVersion` (`app.0.1`) for ordinary changes — it tracks the Hosty
  manifest *contract* format, not this app.

Each runtime app versions independently from Hosty Core/CLI and from the other apps.

## Documentation
- Planning and reference docs live in [`docs/`](docs/root.md); start at
  `docs/root.md`. Each subsystem has a feature doc under `docs/features/`.
- A feature doc opens with a `Status:` / `Created:` / `Updated:` header, then a
  `## Description`, and ends with `## Testing Expectations`. Keep it in sync with the
  code it documents.

## Unit Testing
- Use `xUnit` for backend unit tests.
- Use `Imposter` for mocking dependencies in tests.
- Endpoint tests host `MapTranscodeEndpoints` on an in-memory
  `Microsoft.AspNetCore.TestHost` server with a mocked `ITranscodeEngine`, so no
  ffmpeg process ever starts.
- Ensure all new features have corresponding unit tests.

## ffmpeg and hardware
- The engine shells out to `ffmpeg` / `ffprobe`; it never links them. Argument
  construction (`FfmpegTranscodeEngine.BuildArguments`) is pure and unit-tested — add
  a case there for any new encode option rather than only exercising it end-to-end.
- Hardware acceleration is best-effort and always degrades to software: an explicit
  encoder the host cannot satisfy falls back to `libx264`/`libx265` with a warning,
  never a hard failure. `GET /hardware` and a job's `effectiveHardware` report what
  was actually selected — keep those honest.
- The default `docker` profile carries **no** device and must start on any host
  (incl. macOS Docker Desktop). Hardware lives behind the opt-in `docker-vaapi`
  profile (`/dev/dri` passthrough) and the `local` native runtime (VideoToolbox / AMF).
