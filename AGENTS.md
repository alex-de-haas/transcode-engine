# Agent Instructions for Transcode Engine project

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
