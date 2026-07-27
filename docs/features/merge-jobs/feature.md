# Merge Jobs

Created: 2026-07-27
Updated: 2026-07-27

A job can name further files whose streams join its output, so a consumer can fold
sidecar dubs and subtitles into a video without running `ffmpeg` itself. The same
request can also rewrite any output stream's language and title, which lets an
operator correct a mislabelled track while it is being written.

Both are extensions of `POST /jobs` rather than a job type of their own: a merge
shares the queue, the SSE stream, the snapshot shape and cancellation, and a
separate type would duplicate all of that to express nothing but a narrower set of
valid options — which validation already says.

## Additional inputs

```json
{
  "inputMountLabel": "movies",
  "inputPath": "FMA/Fullmetal Alchemist Brotherhood S01E01.mkv",
  "outputPath": "FMA/Fullmetal Alchemist Brotherhood S01E01 - merged.mkv",
  "audioStreamIndexes": [1],
  "additionalInputs": [
    { "path": "FMA/S01E01.rus.AniDUB.mka", "audioStreamIndexes": [0] },
    { "path": "FMA/S01E01.rus.ass", "subtitleStreamIndexes": [0] }
  ]
}
```

Naming any additional input **makes the job a merge**, which is a stream copy by
definition — the caller does not have to also say `videoCodec: "copy"`, and the
encode-only knobs (`maxHeight`, `crf`) are rejected exactly as they are for an
explicit copy.

Each input resolves against the media mount its `mountLabel` selects, defaulting
to the primary input's mount, and must exist and differ from the output. The
failures read the same as the primary input's, because they are the same checks.

Selections are explicit absolute indexes **within that file**, and at least one
stream must be selected. They are explicit for the same reason a chosen default
track needs an explicit list: the engine turns each into an output position, which
it can only do from a known list.

### Output order and positions

Streams are mapped in a fixed order — the primary's video, then every selected
audio track (the primary's own first, then each additional input's in turn), then
the subtitles the output can carry, then the primary's attachments:

```text
-map 0:v:0  -map 0:1 -map 0:2  -map 1:0  -map 2:0  -map 0:3  -map 0:t?
```

That order is what assigns output positions, and `-disposition` and `-metadata`
address those positions. Chapters ride along from the first input as ffmpeg's
default.

Subtitles still ride only in Matroska outputs — mkv carries any subtitle and
attachment codec, so a stream copy always works, while other containers reject
most on copy and would fail the whole job. A subtitle selection on a non-`.mkv`
output is refused rather than silently dropped.

## Metadata overrides

```json
"metadataOverrides": [
  { "input": 1, "streamIndex": 0, "language": "rus", "title": "MVO wMedia" },
  { "input": 0, "streamIndex": 2, "title": "Original" }
]
```

`input` is the ordinal of the file the stream comes from — 0 is the primary input,
1 the first additional input — and `streamIndex` its absolute index in that file.
Overrides apply to **any** job, a plain transcode as well as a merge.

A field left `null` is not written at all, so the source stream's own tag survives:
relabelling one track never freezes the others' metadata. Nothing here is allowed
to be a silent no-op, so an override is refused when it:

- sets neither value — including when both are empty or whitespace, which argument
  construction would skip;
- names a stream the job does not map explicitly;
- repeats a stream another override already names, since only one could be applied;
- targets an **appended** track while the primary selection of that type is left
  implicit. A null selection is one "copy every stream of this type" mapping that
  expands to however many the file holds, so the appended track's output position
  is not knowable here — and writing against the assumed one would relabel a
  primary track instead.

## Progress

Unchanged, and it works for a merge without special handling: ffmpeg emits
`out_time_us` and `total_size` for a stream copy just as it does for an encode, and
the duration probe that job creation performs reads the primary input — the video,
whose length is the merge's length. `fps` is meaningless for a copy and simply
stays absent, which the snapshot already tolerates.

## Not in scope

- **Normalizing a sidecar in place** — rewriting an untagged `.ac3` into a tagged
  `.mka` without merging it into a video. The consumer keeps sidecars exactly as
  they arrived, so nothing needs it.
- **A cheaper metadata-only edit.** Changing nothing but a title still rewrites the
  file. Matroska keeps track names in its header and could in principle be edited
  in place, but that is a different mechanism needing its own endpoint.

## Testing Expectations

- `MergeJobArgumentTests` — argument construction: every additional input becoming
  an ffmpeg input after the primary; the full map order across inputs; the stream
  copy of every track; an appended track's metadata landing on its own output
  position; a primary stream being relabelled; a null field not being written; a
  subtitle override addressing the subtitle positions; a default track addressing
  positions across inputs; overrides on a plain transcode; and a non-Matroska
  output dropping subtitles and their overrides together.
- `TranscodeJobEndpointTests` — validation: an additional input selecting no
  streams, a missing additional input, an encode-only knob on a merge, an override
  of an unmapped stream, an override naming an input the job does not have, an
  empty override, one carrying only whitespace, two overrides for one stream, an
  override on an appended track without an explicit primary list, and an accepted
  merge reaching the engine as a copy with its resolved paths.
