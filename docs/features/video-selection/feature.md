---
created: 2026-10-12
updated: 2026-10-12
summary: Exact video track selection and metadata preservation for remux, conversion and Dolby Vision jobs.
---

# Explicit Video Selection

`POST /jobs` accepts optional `videoStreamIndex`: an absolute stream index in the
primary input. It applies to composed remux, encode and Dolby Vision rewrite jobs.
Omitting it retains legacy `0:v:0` mapping. `GET /hardware` advertises
`explicitVideoSelection: true`; consumers can refuse unsupported older engines.

The endpoint rejects negative indexes and extraction combinations. Before admitting
a job, the engine probes and verifies that the selected index is video and explicit
audio/subtitle indexes have the requested type. A failed probe cannot silently
substitute another video. The selected video supplies pixel format and Dolby Vision
facts; encoding hardware setup never takes them from another stream.

`metadataOverrides` can address the explicitly mapped video in input zero. Its
language/title applies to video output position zero. Empty title clears a tag;
null retains the source title. Nonempty whitespace-only overrides remain invalid.
Excluded tracks and ambiguous implicit maps cannot receive metadata overrides.

Explicit-selection jobs preserve existing dispositions using no-op `+0` settings,
preventing ffmpeg from inventing a replacement default after the original default
is omitted. Explicit caller default overrides still take precedence. Legacy jobs
retain their existing disposition policy.

## Dolby Vision

The selected video is probed for profile 7, frame rate, language, title and
Default/Forced flags. Its ordinal among probed videos, excluding cover attachments, selects the corresponding
MKVToolNix video track ID; mismatched video counts fail rather than guessing. The
tracks stage excludes all video. The chosen track goes through extraction and RPU
rewriting; the final mux restores its metadata/dispositions and applies its title
or language override. Clearing its name also works at this final mux.

## Testing Expectations

- `VideoSelectionTests`: explicit copy/encode mapping, title overrides and clearing,
  failed/wrong-type probes, Dolby Vision track identity and final mux metadata.
- `TranscodeJobEndpointTests`: negative selection, selected/excluded video names and
  exact internal request propagation; legacy endpoint cases remain covered.
- `VideoSelectionIntegrationTests`: with `FFMPEG_PATH` and `FFPROBE_PATH`, generate a
  multi-video/audio/subtitle container plus a sidecar, run the real engine for copy
  and encode, and probe the output. Assert selected-video dimensions, exact stream
  count, title/language tags, Default/Forced preservation, wrong-type refusal and
  unchanged source bytes.
