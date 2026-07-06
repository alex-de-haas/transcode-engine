# transcode-engine — Code Review

- **Date:** 2026-07-06
- **Baseline:** `main` @ `9661b89`
- **Scope:** entire repo — `src/TranscodeEngine.Api` + tests, `manifest.json`, `Dockerfile`, `docker/`, `.github/workflows`, docs (light pass), git hygiene.
- **Method:** full static read of every source file, key findings re-verified line-by-line in the main session. No builds/tests were run.

**Severity scale.** *Critical* — remote-unauthenticated compromise or guaranteed data loss. *High* — real data-loss/security/deadlock path or actively misleading behavior. *Medium* — correctness/robustness with a plausible trigger. *Low* — hardening, UX, hygiene.

**Totals:** 0 Critical / 1 High / 6 Medium / 9 Low.

---

## Executive summary

Small, well-factored codebase with unusually good docs↔code agreement. The classic attack surface for a media transcoder is closed: process spawning uses `ProcessStartInfo.ArgumentList` with `UseShellExecute=false` everywhere (no shell, resolved absolute paths can never be misread as ffmpeg options), and request paths are resolved and containment-checked before reaching the engine. `BuildArguments` is a pure, unit-testable function. The cancel/start race is handled correctly (`volatile CancelRequested` + re-check after attach).

The real risks are around **output-file data safety**, **stalled-process handling**, **unbounded job retention**, and **CI/publish gating** — plus the acknowledged lack of endpoint auth, whose blast radius is worth restating because the rw media mount *is* the consumer's library.

The single most important fix is H1: encode to a temp file and rename on success, so a failed encode can never destroy a pre-existing destination file.

---

## High

### H1 — A failed/cancelled encode can destroy a pre-existing destination file (data loss)

- `src/TranscodeEngine.Api/Transcoding/FfmpegTranscodeEngine.cs:269` — args always include `-y` (overwrite); ffmpeg truncates any existing file at `outputPath` the moment the encode starts. *(Verified: `var args = new List<string> { "-hide_banner", "-nostdin", "-y" };`.)*
- `FfmpegTranscodeEngine.cs:249-255` — the `finally` block deletes the output path on `Cancelled`/`Failed`: `if (job.State is JobState.Cancelled or JobState.Failed) TryDeleteOutput(job.Request.OutputPath);`. *(Verified.)*
- `src/TranscodeEngine.Api/Api/TranscodeEndpoints.cs:101-109` — the endpoint validates input-exists and output≠input, but never that the output does **not** already exist. *(Verified.)*

**Failure scenario.** A consumer re-encodes `Movie.2160p.mkv → Movie.mkv` where `Movie.mkv` already exists (or has a path bug). ffmpeg truncates the existing file at start; the encode then fails at 90 % (disk full, codec error, or host sleep → shutdown-cancel at `FfmpegTranscodeEngine.cs:210-217`); the cleanup deletes the file outright. The original is gone, not merely a partial artifact. Note the shutdown path takes the same branch — a host restart mid-job deletes the target file.

**Recommendation.** Encode to a temp name in the same directory (`{output}.part-{jobId}`), atomically rename onto `outputPath` only on exit code 0, and delete only the temp file on failure/cancel. Optionally reject `File.Exists(outputPath)` at the endpoint unless an explicit `overwrite` flag is set. This one change also fixes "crash mid-job leaves a partial file that looks complete" and neutralizes L5.

---

## Medium

### M1 — No job watchdog/timeout: one stalled ffmpeg starves the whole engine
`FfmpegTranscodeEngine.cs:206-217` — a job ends only on process exit, explicit cancel, or shutdown; no wall-clock or no-progress timeout. `ProbeDurationAsync` (`:530-575`) likewise has no timeout. `File.Exists` (`TranscodeEndpoints.cs:101`) returns true for FIFOs/special files on Unix, on which ffprobe/ffmpeg block forever. With default `MAX_CONCURRENT_JOBS=1` (`TranscodeEngineSettings.cs:34`), a VAAPI hang / NFS stall / planted FIFO leaves ffmpeg alive-but-silent and every subsequent job queues forever, surfaced as nothing. **Fix:** per-job no-progress watchdog (kill if no `-progress` line for N minutes — the timestamps already flow through `ApplyProgressLine`) + a hard linked-CTS timeout (~30 s) on the ffprobe call.

### M2 — Terminal jobs retained forever and re-broadcast every 1.5 s
`FfmpegTranscodeEngine.cs:17` — `_jobs` is trimmed only by explicit `DELETE /jobs/{id}`. `src/TranscodeEngine.Api/Realtime/TranscodeProgressBroadcaster.cs:29-32` — the periodic tick publishes a `progress` event for **every** job including terminal ones, forever. After 5,000 jobs the engine pushes 5,000 SSE frames per tick to every subscriber; each 256-slot `DropOldest` channel (`TranscodeEventStream.cs:23-24`) then thrashes, dropping the frames that matter, and memory grows unbounded. **Fix:** skip terminal jobs in the periodic tick; evict terminal jobs after a TTL (~1 h) or cap retained history.

### M3 — No authentication on any endpoint (blast radius restatement)
`TranscodeEndpoints.cs` (all routes), `Program.cs:45-50`; acknowledged in README:47-51 / `docs/features/control-api.md:16-18`. Any process reaching the loopback-published port can: delete arbitrary files inside the rw media mounts (`DELETE /jobs/{id}?deleteOutput=true` on a job whose output resolves to an existing file — no traversal needed, the mount *is* the library), fill the disk, and spawn unlimited ffmpeg/ffprobe via the unbounded queue (`FfmpegTranscodeEngine.cs:18`). **Fix:** interim shared-secret header from a Core-injected env var until platform cross-app auth ships; add a queued-jobs bound.

### M4 — `GET /events` never flushes headers while idle → clients hang "connecting"
`TranscodeEndpoints.cs:144-158` — headers are set but nothing is written/flushed before `await foreach`; with zero jobs no bytes leave the server, `EventSource` never fires `open`, and cloudflared/nginx idle timeouts drop the connection. **Fix:** `await context.Response.Body.FlushAsync(ct)` immediately after setting headers; emit a `: keepalive\n\n` comment every ~15 s.

### M5 — Image publish is not gated on tests
`.github/workflows/publish.yml:10-14` triggers on every push to `main` independently of `ci.yml`; a commit with failing tests still ships `ghcr.io/...:latest`, which fresh installs pull. **Fix:** add the test step to publish.yml before build, or trigger publish via `workflow_run` on CI success.

### M6 — Container runs as root; path containment is lexical only
`Dockerfile` has no `USER` directive — the app and ffmpeg (fed remote-controlled arguments) run as root with rw media mounts. `TranscodeEngineSettings.cs:123-131` contains paths via a `Path.GetFullPath` string-prefix check that does not resolve symlinks, so a symlink planted in the shared mount (writable by the consumer app too) redirects ffmpeg outside the root — as root, anywhere in the container FS. **Fix:** `USER app` (the aspnet base ships the non-root `app`/`$APP_UID`; for `docker-vaapi` add the render group for `/dev/dri`); optionally compare against realpath-resolved parents.

---

## Low

- **L1.** Stale `pullPolicy` in `manifest.json:24,31` — Core intentionally ignores it (`docker-host .../RuntimeAppManifest.cs:554`). Remove.
- **L2.** ffprobe stderr redirected but never drained (`FfmpegTranscodeEngine.cs:534-561`); also `WaitForExitAsync` (`:208`) doesn't guarantee the async output callbacks drained, so the failure-tail (`:234`) may miss the real error line. Don't redirect ffprobe stderr, or drain it; add a sync `WaitForExit()` for EOF before reading the tail.
- **L3.** `TryKillProcess` filter misses `Win32Exception` (`:591-604`) — a kill race (notably Windows/AMF) propagates to a 500 from `CancelAsync`. Add it.
- **L4.** Ordinal containment check on case-insensitive filesystems (`TranscodeEngineSettings.cs:126`) false-rejects case-differing paths on macOS/Windows localCommand runtimes. Use `OrdinalIgnoreCase` there or document.
- **L5.** Duplicate output paths across concurrent jobs aren't rejected — with `MAX_CONCURRENT_JOBS>1` two jobs writing one path interleave; the H1 temp-file+rename fix largely neutralizes this, otherwise reject an output path already claimed by an active job.
- **L6.** No disk-space check before starting (ffmpeg runs to ENOSPC, then deletes the partial); compare `DriveInfo.AvailableFreeSpace` vs input size before enqueue.
- **L7.** CI/supply-chain: `ci.yml:18-22` tag-pins actions while `publish.yml` SHA-pins; `Dockerfile:5,15` base images tag-only (no digest); no `HEALTHCHECK` despite `/healthz` existing.
- **L8.** README staleness: `GET /hardware` shape omits `amfAvailable` (`README.md:63`); `hardwareAcceleration` value list omits `amf` (`:76-78`) — both correct elsewhere.
- **L9.** `.gitignore` has no `.claude/` entry though `.claude/settings.local.json` exists untracked — add one to prevent accidental commit. Otherwise git hygiene is clean (no artifacts/secrets/.DS_Store tracked).

---

## Architecture observations

- Clean layering: the endpoint owns all validation and path resolution; the engine receives only pre-resolved absolute paths. No injection surface found.
- Job state is deliberately in-memory only; a restart loses queued/running jobs and the shutdown path *deletes* in-flight partial outputs. The consumer's reconcile/resubmit logic is load-bearing and deserves a contract note in `consumer-integration.md`.
- SSE fan-out (per-subscriber bounded `DropOldest` channel + self-healing full-snapshot ticks) is the right shape — it just needs M2/M4 to stay healthy long-term.
- Telemetry follows the platform convention exactly (gate on `OTEL_EXPORTER_OTLP_ENDPOINT`, parameterless exporters, no localhost fallback), and the `docker-vaapi` data-target resolves correctly via profile-type matching.

## Test gaps (by value)

1. **Progress parsing / snapshot math (zero coverage)** — `ApplyProgressLine` (`out_time_us`/`out_time_ms` quirk, `speed` `x`-strip, `N/A`) and `ToSnapshot` (percent clamp, ETA null rules, complete-pins-100 %) are pure and are the consumer-facing contract.
2. **Path containment edge cases** — current tests cover relative `../` only; add absolute-outside-root, absolute-inside-root, root-equals-path, trailing-separator roots.
3. **`BuildArguments` hardware paths** — VAAPI device, AMF `d3d11va`+nv12, VideoToolbox encoders, CRF emission all untested.
4. **Endpoint validation matrix** — crf/maxHeight range, negative stream indices, invalid codec/hardware, output==input, cancel/remove/GET, SSE handler.
5. **Job lifecycle** — queued-cancel skip, cancel-during-start, terminal cleanup; feasible with `FFMPEG_PATH` pointed at a stub script, which would also lock in the H1 fix.
6. **`FromConfiguration`** env precedence / `MAX_CONCURRENT_JOBS` flooring.

## Priority

1. **H1** temp-file + rename-on-success (data safety) — small diff, highest value.
2. **M1** ffprobe/job timeouts + watchdog.
3. **M3** interim shared-secret auth + queue bound.
4. **M2 / M4** SSE health (skip terminal jobs, idle flush + keepalive).
5. **M5** gate publish on CI; **M6** non-root container.

---

## Resolution (applied 2026-07-06)

**Fixed**

- **H1** — Encode to a hidden temp file beside the destination (`.{name}.{jobId}.part{ext}`, extension
  preserved for muxer inference) and atomically rename onto the output only on exit code 0; a
  failed/cancelled/interrupted encode now only ever deletes the temp, never the pre-existing output.
  (`FfmpegTranscodeEngine.RunJobAsync` / `TempOutputPath` / `TryPublishOutput`, `BuildArguments` gains an
  optional destination arg.)
- **M1** — Per-job no-progress watchdog (kills ffmpeg after 5 min of silence via a linked CTS reset on each
  `-progress` line) + a 30s hard timeout on the ffprobe duration call.
- **M2** — Periodic tick skips completed/failed jobs (cancelled still tick, as they have no dedicated
  event); terminal jobs evicted after ~1h / a 500-job cap (`PruneTerminalJobs`, `TranscodeJob.CompletedAt` /
  `IsTerminal`).
- **M4** — `GET /events` flushes a `: connected` comment on connect and a `: keepalive` every 15s while idle.
- **M5** — `publish.yml` now has a `test` job that `build` needs, so a failing commit can't ship `:latest`.
- **M3 (partial)** — Job queue bounded (1024) so a POST flood can't grow it unbounded. Endpoint auth itself
  is **deferred** (see below).
- **L1** — `pullPolicy` removed from `manifest.json`.
- **L2** — ffprobe stderr no longer redirected (silenced by `-v quiet`, so no undrained pipe); a blocking
  `WaitForExit()` after the async wait drains the callbacks before the failure tail is read.
- **L3** — `TryKillProcess` filter now includes `Win32Exception` (Windows/AMF kill race).
- **L8** — README `/hardware` shape and hardware/`effectiveHardware` lists now include `amf` / `amfAvailable`.
- **L9** — `.gitignore` ignores `.claude/settings.local.json`.

Docs kept in sync: `transcode-engine.md` (bounded queue, probe timeout, temp-file/watchdog/rename lifecycle,
retention), `control-api.md` (progress-tick terminal-skip, SSE connect/keepalive comments).

**Deferred (rationale)**

- **M3 (auth)** — Adding endpoint auth contradicts the documented *trusted single-tenant, non-public
  endpoint* posture (README TODO / `consumer-integration.md` / `control-api.md`) and needs a Core-injected
  secret contract; it belongs with the planned cross-app auth work, not a unilateral change here.
- **M6 (non-root container)** — Real fix, but `USER $APP_UID` risks breaking writes to the Hosty-managed
  `/app/data` mount and `/dev/dri` access (host-specific render GID) and can't be validated without a
  container run against a real Hosty deployment. Needs a dedicated, tested change.
- **L4** — The lexical containment check is `Ordinal`, which is correct and strict on the primary Linux
  docker runtime (case-sensitive FS); switching to `OrdinalIgnoreCase` there would *loosen* it. Only
  false-rejects case-differing paths on the macOS/Windows `localCommand` runtimes — left as a known,
  low-value edge until made OS-aware.
- **L5** — Largely neutralized by H1: each job writes a unique `.{jobId}.part` temp, so concurrent jobs to
  one output no longer interleave the same file (only the final rename races, last-writer-wins).
- **L6** (pre-flight disk-space check) and **L7** (HEALTHCHECK, base-image digest pinning) — lower value;
  HEALTHCHECK also needs a curl/wget in the runtime image. The new `test` job's actions are SHA-pinned.
