# Dolby Vision Conversion

Created: 2026-09-04
Updated: 2026-09-04

A video-copy job can rewrite a dual-layer Dolby Vision **profile 7** source — every UHD
Blu-ray remux — into single-layer **profile 8.1**, and the probe reports the Dolby Vision
configuration record that tells the two apart. Profile 7 is the one form of Dolby Vision
that Apple TV and Infuse cannot decode and quietly play as HDR10; profile 8.1 is the form
they play as Dolby Vision. The picture is copied byte for byte, the RPU metadata is
rewritten, and the enhancement layer — measured at 1.6 % of such a file in
[compression controls](../compression-controls/feature.md#dolby-vision-does-not-survive-a-re-encode)
— is dropped. Nothing is re-encoded.

Consumer: media-server's
[dolby-vision-profile](https://github.com/alex-de-haas/media-server/blob/main/docs/features/dolby-vision-profile/plan.md).

## The probe reports the record

A video stream's `dolbyVision` in `POST /probe` carries the configuration record ffprobe
reads out of the container — `profile`, `level`, `blSignalCompatibilityId`, `rpuPresent`,
`elPresent`, `blPresent` — or null when the stream has none. `hdr` keeps answering
`DolbyVision`; the record sits beside it, because the flat answer cannot say what a
consumer needs to decide: a profile 7 with `elPresent: true` and compatibility id 6 plays
as HDR10 on Apple hardware, a profile 8 with compatibility id 1 plays as Dolby Vision, and
a profile 5 has no viewable base layer at all. See [Probe API](../probe-api/feature.md#dolby-vision).

## The job option

`POST /jobs` takes `dolbyVision`: absent or `keep` (the default, and exactly the job a
caller got before the field existed) or `toProfile81`. The endpoint refuses `toProfile81`
with `400` and a reason when the video is re-encoded (a re-encode drops Dolby Vision
whatever is asked — set `videoCodec: copy`, or leave a merge's codec unnamed), on an
extraction, when the input or the output is not `.mkv`, or when the tools are not in the
image. The engine then refuses an input whose video is not profile 7 — a profile 8 is
already what the conversion produces, a profile 5 has nothing to fall back on, and a stream
without a record has no Dolby Vision, and a file without a video stream has nothing to
convert — from the same probe it already runs at create time, as the same `400`. An input
that probe could not read at all is let through: the check after the last stage decides,
which degrades like every other probe here rather than refusing over a timeout.

Everything else about the job is unchanged: audio and subtitle selection, defaults,
merged inputs, audio targets and metadata overrides all apply, and the output is published
by the same temp-and-rename protocol.

## How it runs

ffmpeg cannot do this. Profile 7 keeps its RPU in the enhancement layer, which Matroska
stores in per-block `BlockAdditions`; rewriting it is `dovi_tool`'s job, and writing the
Matroska mapping that announces the result is `mkvmerge`'s. So the picture takes a path of
its own while ffmpeg composes everything *but* the picture, and the two meet in mkvmerge —
four stages, each one process under the job's cancel and no-progress watchdog:

1. **The tracks.** ffmpeg composes the audio, subtitles, attachments, merged inputs, audio
   targets, metadata overrides and default flags exactly as any other job would, into a
   hidden intermediate Matroska with no video stream — the argument list is the composed
   job's minus `-map 0:v:0` and `-c:v`, plus `-vn`, so nothing is copied that is about to be
   discarded. A request that selects nothing (empty audio and subtitle lists, no merged
   input, or null selections on an input with nothing of the kind) skips this stage
   altogether and the output is the converted video alone: ffmpeg given no `-map` would
   select streams on its own, reintroducing the tracks the caller excluded and, without
   `-vn`, re-encoding the picture into a file about to be thrown away. The input's streams
   are listed for that decision; an unreadable list runs the stage, which fails honestly if
   it turns out empty.
2. **The layers.** `mkvextract input.mkv tracks ID:layers.hevc` writes the video track as an
   Annex B elementary stream with base and enhancement layer interleaved, which is how
   mkvextract writes a track carrying `BlockAdditions`. The track id comes from
   `mkvmerge --no-bom -J`, not from ffprobe's stream index: attachments are streams to
   ffprobe and not tracks to mkvmerge, so the two numberings can differ. Every tool's
   output is read as UTF-8 whatever the host's console code page — on Windows .NET's
   default is that code page, under which mkvmerge's JSON was mojibake and its byte-order
   mark three stray characters, and a real job failed there — and a byte-order mark ahead
   of the document is skipped regardless. When no video track can be read, the log carries
   what mkvmerge printed, so the next such failure is diagnosable without reproducing it.
3. **The rewrite.** `dovi_tool -m 2 convert --discard layers.hevc -o dv81.hevc` rewrites
   every RPU to profile 8.1 with base-layer compatibility id 1 and drops the enhancement
   layer; the base layer is copied. The source layers are deleted as soon as this stage
   ends, which keeps the peak footprint at two copies of the video rather than three.
4. **The output.** `mkvmerge --output … [--default-duration 0:R/Dfps] [--language 0:L]
   [--track-name 0:T] dv81.hevc --no-video tracks.mkv` assembles the file, the video first.
   An elementary stream carries no timestamps, so the default duration is the source's
   frame rate — without it mkvmerge assumes 25 fps and a 23.976 film drifts a minute over
   two hours — and no tags, so the video's language and title are put back from the source
   probe. mkvmerge reads the RPU and writes the Matroska Dolby Vision mapping itself. Exit
   code 1 is "finished with warnings" and still wrote the file; only 2 fails the job.

Before the first stage the job fails early when the output volume has less free space than
twice the input, and after the last it probes the output: a file that is not profile 8 with
compatibility id 1 fails the job rather than being published under a name that promises
what it does not carry. The three intermediates are hidden files beside the output
(`.{name}.{jobId}.tracks.mkv`, `.bl-el.hevc`, `.dv81.hevc`) and are removed on success,
failure and cancel; a pre-existing file at the output path is never touched except by the
final rename.

### Progress and liveness

The snapshot's single `percentComplete` is split evenly across the four stages, each being
one pass over roughly the whole file. Only the first stage speaks ffmpeg's `-progress`
dialect; the others are measured by the growth of the file they write against the size it
is expected to reach (the input's size for the extraction, the extracted stream's for the
rewrite, the sum of the two intermediates for the mux), polled once a second. Growth is also
what extends the no-progress watchdog: `dovi_tool` draws its progress bar only on a
terminal and would look hung on a pipe for the whole of a two-hour film. `etaSeconds` is
withheld for such a job — a ratio across tools that read and write at different speeds
would be a number nothing could stand behind. `outputSizeBytes` is the growing output
during the last stage and the published file's size on completion.

## The tools

The image installs `mkvtoolnix` from apt and a pinned `dovi_tool` release, fetched in the
build stage per `TARGETARCH` and checked against the SHA-256 GitHub publishes for the asset
(see [Build and deployment](../build-and-deployment/feature.md)); together they add 31.7 MB to the
image, measured on a `linux/arm64` build. `DOVI_TOOL_PATH`, `MKVMERGE_PATH`
and `MKVEXTRACT_PATH` point the native `local` runtime at host installs
([Configuration](../configuration/feature.md)).

`GET /hardware` reports them under `tools` — `{ dolbyVisionConversion, doviTool,
mkvtoolnix }`: a flag a consumer gates its UI on, and the two versions. Availability is a
PATH scan and three `File.Exists` calls; the versions come from spawning each tool once
with `--version`, so they are read lazily on the first `GET /hardware` and kept. An engine
without the tools refuses `toProfile81` rather than silently copying, which is the same
honesty `effectiveHardware` keeps about encoders.

## Testing Expectations

Backend tests use xUnit and Imposter; no process ever starts.

- `DolbyVisionConversionTests` — the ffmpeg stage mapping no video, naming no video codec
  and carrying `-vn` while keeping the selected tracks, and `keep` still mapping and
  copying the video; the stage skipped when nothing is selected and run for a null
  selection the input can satisfy, for attachments alone, for an explicit selection, for a
  merge, and for an unreadable stream list; the argument builders for `mkvextract`,
  `dovi_tool` and `mkvmerge` (default duration, language and title present and omitted,
  the video first, and the video alone without a composition); the first video track taken
  from `mkvmerge --identify`, null without one or without a document, and found behind a
  byte-order mark or leading whitespace; the identify arguments asking for JSON without a
  byte-order mark; the create-time
  refusal of profile 8, profile 5, a recordless stream and a video-less input by name,
  profile 7 accepted, and an unreadable input let through; the output check accepting only profile 8 with
  compatibility id 1; the free-space rule needing twice the input and only when both
  figures are known; the stage-to-percentage mapping and its clamp; the intermediates'
  names beside the output; `ParseSourceProbe` reading the record, the frame rate and the
  tags, and treating `0/0` and `und` as nothing; the job's reported progress replacing the
  ffmpeg derivation, withholding the ETA and reading 100 on completion; and tool discovery
  — a bare name on the PATH, an explicit file, a missing one, the version parsed from each
  tool's banner, and availability requiring all three.
- `DolbyVisionJobEndpointTests` — `dolbyVision` parsed case-insensitively with absent and
  `keep` as the default needing no tools; `toProfile81` refused with a re-encode, on an
  extraction, on a non-`.mkv` output, on a non-`.mkv` input, and on an engine without the
  tools, each naming the reason; accepted on a copy and on a merge that names no codec, with
  the mode on the request the engine receives; the engine's refusal of a non-profile-7
  input arriving as the same `400`; and `HardwareStatus` carrying the tooling.
- `FfprobeMediaInspectorTests` — the record read field by field for a profile 7 disc remux
  and a profile 8.1 stream, and null for a stream without one or with a typed entry that
  names no profile.

Not covered by tests, because no test fixture can be a UHD Blu-ray: that `mkvextract` writes
the enhancement layer into the elementary stream, that `dovi_tool` produces compatibility
id 1, and that the output plays as Dolby Vision on an Apple TV 4K. What has been checked
without such a file is the image itself — `dovi_tool 2.3.3` and MKVToolNix 82.0 present,
`GET /hardware` reporting them, and a `toProfile81` request passing the tooling check. The
end-to-end run on a real profile 7 source is the consumer's
[verification step](https://github.com/alex-de-haas/media-server/blob/main/docs/features/dolby-vision-profile/plan.md#verification-steps)
and has not been run in this repository.
