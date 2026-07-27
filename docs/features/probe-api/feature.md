# Probe API

Created: 2026-07-27
Updated: 2026-07-27

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
      "width": 1920, "height": 1080, "frameRate": 23.976, "bitDepth": 10,
      "hdr": "Hdr10", "channels": null, "sampleRate": null }
  ]
}
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
  mastering metadata; HDR answered only for video; embedded cover art keeping its
  index so the numbering matches `ffprobe`; the track name read from either
  container's tag; `und` becoming no language; unreadable rational frame rates;
  an unmodelled stream kind still occupying its index; and output without streams
  yielding no result.
- `ProbeEndpointTests` — mount selection and the failure responses against the
  real endpoint wiring with a mocked inspector: unknown label, missing file, empty
  path, a path escaping its mount, a successful probe returning the normalized
  description, and a file that is not media answering 400 rather than an empty
  result. Plus the wire format, asserted on the **raw body**: reading the response
  back into `ProbeResponse` would round-trip an ordinal happily and prove nothing.
