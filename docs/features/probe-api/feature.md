# Probe API

Created: 2026-07-27
Updated: 2026-09-04

`POST /probe` inspects one media file on a media mount and returns a normalized
description of its container and streams. It exists so a consumer does not have to
ship `ffprobe` of its own: this image already carries it for the duration probe
that job creation performs, and the endpoint exposes that capability rather than
duplicating it.

Unlike `POST /jobs`, this is an inspection call rather than a job — the answer is
in the response, and nothing is queued, streamed or polled.

## Request

```json
{ "mountLabel": "movies", "path": "The Rock (1996)/The Rock (1996).m4v" }
```

`path` is resolved through `TranscodeEngineSettings.ResolveMediaPath`, exactly as
a job's `inputPath` is: `mountLabel` selects the media root by its Hosty label,
and is optional when the engine has exactly one mount. The failure responses match
job creation's for the same mistakes — an unknown label, a path escaping its
mount, and a file that does not exist are each a 400 carrying the reason.

One file per call. `ffprobe` dominates the cost of a probe, so a batch form would
buy partial-failure semantics, response-size limits and a timeout spanning a whole
pack in exchange for a small fraction of the time. A consumer with many files to
probe issues several requests concurrently instead, which keeps failure handling
per file.

## Response

```json
{
  "container": "mkv",
  "durationSeconds": 7506.291,
  "bitrate": 7699019,
  "sizeBytes": 7223885097,
  "streams": [
    { "index": 0, "kind": "Video", "codec": "hevc", "profile": "Main 10",
      "language": "eng", "title": null, "isDefault": true, "isForced": false,
      "bitrate": 7500000, "width": 1920, "height": 1080, "frameRate": 23.976,
      "bitDepth": 10, "hdr": "Hdr10", "channels": null, "sampleRate": null,
      "dolbyVision": null }
  ]
}
```

A Dolby Vision stream fills `dolbyVision` with its configuration record:

```json
"hdr": "DolbyVision",
"dolbyVision": { "profile": 7, "level": 6, "blSignalCompatibilityId": 6,
                 "rpuPresent": true, "elPresent": true, "blPresent": true }
```

Enums cross the wire **by name** — `"Video"`, `"Hdr10"` — carried by the contract
types themselves rather than by a host's serializer configuration, so the promise
holds wherever they are serialized. Ordinals would also make reordering a member a
silent breaking change for anyone already deployed.

`container` is the file's extension, not `ffprobe`'s `format_name` — that field is
the demuxer's format list (`matroska,webm`), which names a family rather than a
container.

The response is a deliberate translation, not a passthrough of `ffprobe`'s JSON.
A consumer compares these fields against its own header parser, so this API owns
the vocabulary; pinning `ffprobe`'s schema into the contract would make anything
built on it silently unavailable wherever that parser is the only provider.

### Stream indexes are ffprobe's

`index` is the absolute stream index `ffprobe` reports, **including the entry it
synthesizes for embedded cover art**. `The Rock (1996).m4v` holds ten `trak` boxes
but yields eleven streams: the artwork sits at index 1 and moves every audio track
by one. Job creation addresses audio and subtitle streams by these indexes, so a
consumer that mixes engine-probed and locally-parsed sources would otherwise
select the wrong track. Streams whose kind this API does not model are reported as
`Other` rather than dropped, for the same reason.

### Track names

Matroska keeps a track's name in `title`; MP4 keeps it in `udta/name`, which
`ffprobe` surfaces as the `name` tag. Whichever the file carries becomes `title`.
A language of `und` is `ffprobe` saying it has none, and becomes `null`.

### Stream bitrate

`bitrate` is one stream's own rate in bits per second, and it is what lets a consumer
say what a single track costs — the number a UI needs to show that nineteen lossless
dubs, not the picture, are the larger half of a file.

`ffprobe`'s `bit_rate` is the direct answer, and MP4 and TS carry it. **Matroska does
not**, so the field is absent on exactly the remuxes worth measuring; `mkvmerge`
instead writes a per-track `BPS` statistics tag, which is read as the fallback.
`ffmpeg` appends the tag's language when the file sets one, so a remux commonly
spells it `BPS-eng` and the plain name is missing — both are read.

A stream stating neither reports `null`. The overall `bitrate` is known and the
stream count is known, but a share of the whole is a guess, and a consumer cannot
tell a guess from a measurement once it is in the field. A zero from `ffprobe` is
`ffprobe` declining to answer and is reported as `null` too, not as a measured zero.

### HDR

`hdr` carries everything this app can determine — `DolbyVision`, `Hdr10Plus`,
`Hdr10`, `Hlg`, `Sdr` — and is not narrowed to what a weaker provider could
reproduce. It also has an explicit `Unknown` member, distinct from `Sdr`, so a
consumer's header-only parser can fill the same field honestly instead of having
its silence read as "not HDR"; this app itself should never need it.

The transfer function decides: `smpte2084` is PQ, `arib-std-b67` is HLG, anything
else known is SDR. A Dolby Vision configuration record on the stream outranks the
transfer function. `Hdr10Plus` is claimed only when the stream carries SMPTE
2094-40 side data — a file whose dynamic layer lives purely in frame side data
reads as `Hdr10`, under-reporting rather than guessing.

### Dolby Vision

`DolbyVision` alone cannot say what a consumer has to decide, so a video stream that
carries the record also reports it, field by field, as `dolbyVision`: `profile`,
`level`, `blSignalCompatibilityId`, `rpuPresent`, `elPresent`, `blPresent` — the
same 24 bytes the container holds in an MP4 `dvcC`/`dvvC` box or a Matroska
`BlockAdditionMapping`, read through `ffprobe`'s `side_data_list`. A profile 7 with an
enhancement layer and compatibility id 6 is a UHD Blu-ray remux that Apple TV and
Infuse play as HDR10; a profile 8 with compatibility id 1 plays as Dolby Vision; a
profile 5 has no viewable base layer. The field is null for a stream without a
record, for a record entry that names no profile, and always for anything but video.
It is what [Dolby Vision conversion](../dolby-vision-conversion/feature.md) decides
on, and what a consumer stores to show which kind a file is.

## Bounds

A probe is bounded by the same 30-second timeout job creation applies to its own
duration probe, and the `ffprobe` process is killed when the request is cancelled
or the deadline passes. A FIFO, a special file or a blocked read fails the request
rather than hanging it. Output that cannot be read as media is a 400, not an empty
result.

## Testing Expectations

- `FfprobeMediaInspectorTests` — the translation, over JSON shaped as `ffprobe`
  emits it: the container taken from the extension rather than the demuxer list;
  the overall figures; every HDR case including Dolby Vision side data, SMPTE
  2094-40, an absent transfer function, and a PQ file carrying only static
  mastering metadata; the Dolby Vision record read field by field for a profile 7
  disc remux and a profile 8.1 stream, and null without one or for a typed entry
  that names no profile; HDR answered only for video; embedded cover art keeping its
  index so the numbering matches `ffprobe`; the track name read from either
  container's tag; `und` becoming no language; a stream bitrate read from
  `bit_rate`, from a `BPS` tag and from a language-suffixed one, with `bit_rate`
  winning over the tag, a zero treated as no answer, and neither present staying
  null; unreadable rational frame rates;
  an unmodelled stream kind still occupying its index; and output without streams
  yielding no result.
- `ProbeEndpointTests` — mount selection and the failure responses against the
  real endpoint wiring with a mocked inspector: unknown label, missing file, empty
  path, a path escaping its mount, a successful probe returning the normalized
  description, and a file that is not media answering 400 rather than an empty
  result. Plus the wire format, asserted on the **raw body**: reading the response
  back into `ProbeResponse` would round-trip an ordinal happily and prove nothing.
