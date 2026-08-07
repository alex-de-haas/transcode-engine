# Extract Jobs

Created: 2026-08-07
Updated: 2026-08-07

A job can write selected streams of its input out as **separate files** — one track
per file — so a consumer can pull a dub or a subtitle out of a container without
running `ffmpeg` itself. The exact inverse of [merge jobs](../merge-jobs/feature.md),
and requested by the same consumer for the same reason: `media-server` ships without
`ffmpeg` on purpose.

## Still `POST /jobs`, but this one changes the job model

A merge is an extension of `POST /jobs` because it shares the queue, the SSE stream,
the snapshot and cancellation, and a separate type would duplicate all of that to
express nothing but a narrower set of valid options. An extraction shares the same
machinery and the argument carries over.

What it does not share is the assumption underneath everything else: that a job
produces one file. A single `OutputPath` used to run through the request, the
descriptor, the snapshot, the temp-publish protocol and `RemoveAsync(deleteOutput)`.
All of those now work from `TranscodeJobRequest.OutputPaths` — one entry for a
composed job, one per stream for an extraction — so neither shape needs a special
case of its own.

**One ffmpeg invocation writes every output.** A job per track would barely touch the
engine, but each job re-reads the whole container: nineteen dubs out of a 141 GB
remux would be nineteen full passes over a disk-bound operation. One invocation with
an output group per track reads the file once.

## Request

```json
{
  "inputMountLabel": "movies",
  "inputPath": "Movie (2019)/Movie (2019).mkv",
  "outputs": [
    { "path": "Movie (2019)/Movie (2019).rus.mka", "streamIndex": 3,
      "language": "rus", "title": "AniDUB" },
    { "path": "Movie (2019)/Movie (2019).eng.srt", "streamIndex": 5 },
    { "path": "Movie (2019)/Movie (2019).rus.srt", "streamIndex": 6, "codec": "srt" }
  ]
}
```

`outputs` and `outputPath` are **mutually exclusive**, and naming `outputs` is what
makes the job an extraction. Each entry is
`{ mountLabel?, path, streamIndex, codec?, language?, title? }`, resolving against the
media mount its `mountLabel` selects and defaulting to the primary input's — the same
rule, and the same failures, as every other path in this API.

### One stream per output

`streamIndex` is a single absolute index in the input, not a list. That is the whole
design: it makes each output addressable as itself, which is why the language and the
title sit **on the output entry** rather than going through `metadataOverrides`. An
override's `(input, streamIndex)` pair exists to locate a stream's position in a
composed output; here there is no composition — position 0 of its own file is the only
place the stream can be. An output holding several streams would be a merge with extra
steps, and the API already has a merge.

`metadataOverrides` is therefore **refused** on an extraction rather than quietly
accepted alongside a second way of saying the same thing.

### What an extraction refuses

There is no picture in the output, so everything that describes one is rejected rather
than ignored — the same rule `maxHeight` and `qualityLevel` already follow on a copy:

- `videoCodec`, `maxHeight`, `qualityLevel` — nothing is encoded.
- `additionalInputs`, `audioStreamIndexes`, `subtitleStreamIndexes`,
  `defaultAudioStreamIndex`, `defaultSubtitleStreamIndex`, `audioTargets` — all of them
  describe a single composed output, which this job does not produce.
- `hardwareAcceleration` omitted or `auto` is **accepted and inert**; an explicit
  accelerator is refused. Nothing is decoded or encoded, so there is nothing to
  accelerate — but `auto` is what a client sends by default, and failing the ordinary
  call over a field that means nothing here would be gratuitous. `effectiveHardware`
  reports `none`, because it must stay honest: `software` would claim a software encode
  that never happened.

### `codec` is `copy`, or a text-subtitle conversion

Default `copy`. The only other accepted values are `srt`, `ass` and `webvtt` — text to
text — and the set is closed deliberately. A `mov_text` stream has no file form of its
own and cannot be extracted at all without one conversion; every other case that wants a
different codec wants a composed output, which is what `videoCodec` and `audioTargets`
are for. Re-encoding audio on the way out would make this job type a second, worse
encoder surface.

### Validation at creation, not twenty minutes in

The endpoint checks everything it can see: the refusals above, a non-negative
`streamIndex`, a parseable `codec`, resolvable paths, and outputs that are pairwise
distinct and never the input (two outputs at one path would race each other's publish
and leave whichever finished last, losing a track silently).

The checks that need the input's **streams** run in the engine, where the input is
already probed at create time. They are worth paying for because an extraction is
disk-bound: a stream index ffmpeg rejects is a typo discovered after reading the whole
container.

- `streamIndex` names a real stream, and it is **audio or subtitle**. Video extraction
  is not offered (see Not in scope).
- A text codec applies only to a subtitle stream.
- A **bitmap subtitle aimed at a text file** is refused — `hdmv_pgs_subtitle`,
  `dvd_subtitle`, `dvb_subtitle` and `xsub` into `.srt`/`.ass`/`.ssa`/`.vtt`. Turning
  one into text is OCR, a different mechanism with a different dependency. The same
  stream into `.sup` is fine; only the text extensions are the contradiction.

Those refusals reach the caller as the same `400` envelope as everything else, so a
caller cannot tell which side of the line a rule sat on. A probe that cannot answer
skips the check rather than refusing the job — the same degradation the duration probe
makes, and the job still fails honestly if the request was wrong.

**The `.mkv`-only subtitle gate does not apply.** It exists because a composed output
carries whatever subtitle codecs the input held, and most containers reject them on copy
and fail the whole job. An extracted subtitle is one stream in a file chosen for it,
which is precisely the case the gate was never about — so the gate stays where it is, on
the composed path.

## Arguments

```text
ffmpeg -hide_banner -nostdin -y -i IN -progress pipe:1 -nostats \
  -map 0:3 -c copy -metadata:s:0 language=rus -metadata:s:0 title=AniDUB OUT1 \
  -map 0:5 -c copy OUT2 \
  -map 0:6 -c:s srt OUT3
```

No `-map 0:v:0`, no `-c:v`, and no hardware decode setup — the three things every
composed job emits unconditionally. A `-map 0:v:0` here would put the whole film into
every extracted track's file.

`-progress` and `-nostats` are global options, so unlike the composed path (where they
sit just before the single output, which reads the same) they precede the **first**
output. Everything after that is per-output and is emitted ahead of the file it belongs
to; `-metadata:s:0` addresses that output's only stream, which is what one-stream-per-file
buys. A field the request left null is not written, so the source stream's own tag
survives into the extracted file.

## Publishing several files

Each output gets its own temp path beside its destination (`.{stem}.{jobId}.part{ext}`),
exactly as one does for a composed job, and **nothing is published until ffmpeg exits
successfully** — a cancelled or failed run leaves no file at a real path, only temps,
which are then deleted.

The publish itself is a loop of moves and is therefore not atomic as a set. When a move
fails partway the job is **reported failed, naming the outputs that were published**,
rather than rolled back: the consumer records what exists so nothing sits on disk with
no row pointing at it, and its own contract says a completed job missing an output fails
while importing the rest. Rolling back would delete files the operator asked for to
preserve a tidiness nobody benefits from.

`DELETE /jobs/{id}?deleteOutput=true` deletes every output, and retention treats the set
as one.

## Snapshot and progress

- `JobDescriptor` and `JobSnapshot` carry **`outputPaths`**, a list. The singular
  `outputPath` stays for composed jobs and is `null` for an extraction.
- `name` for an extraction is the **input's** file name. No single output represents the
  job, and `Path.GetFileName(outputPath)` has nothing to point at.
- `fps` is meaningless for a stream copy and stays absent, which the snapshot already
  tolerates. Duration comes from the input probe as it always has.

**`outputSizeBytes` is measured, not read from ffmpeg, whenever a job writes more than
one file.** ffmpeg's `-progress` `total_size` is *one muxer's* byte count, not the run's:
a two-output extraction measured on a 123,120-byte result reported 123,034 — the first
output alone, silently omitting the second. A job with several outputs therefore sums its
own temp files once per progress tick (ffmpeg closes each block with a `progress` key,
which is the cheap place to do it). Composed jobs are untouched and keep reading
`total_size`, which is exact with a single muxer.

One consequence worth knowing: `percentComplete` is derived from `out_time`, which
follows the last packet written across **all** outputs. Extracting a subtitle track that
ends before the film does leaves the percentage short until the job completes, at which
point it reads 100. Nothing is wrong; the shortest stream simply stops advancing the
clock.

## Not in scope

- **Video extraction.** Nothing has asked for it, and a raw elementary video stream is a
  different problem — it is the detour the consumer's Apple-client spike already found to
  be a dead end for Dolby Vision.
- **Bitmap subtitles into text.** Refused above. VobSub in particular needs an
  `.idx`/`.sub` *pair*, which breaks the one-stream-one-file rule this whole shape rests
  on.
- **OCR, or any conversion beyond text to text.** A different mechanism with a different
  dependency.
- **Removing the extracted track from the input.** The input is never rewritten.
  Producing a container without a track is a composed output — an ordinary job with a
  narrower selection.

## Testing Expectations

- `ExtractJobArgumentTests` — argument construction: no video map and no `-c:v`; no
  hwaccel setup even when the worker resolved a family; one map, codec and output token
  per entry; `-c copy` by default and `-c:s srt`/`ass`/`webvtt` when asked; per-output
  `-metadata:s:0` for language and title, and neither written when null; `-progress`
  emitted before the first output; each output written to its own destination; and
  `OutputPaths` covering both job shapes.
- `ExtractJobValidationTests` — the create-time rules as pure functions: an audio and a
  subtitle stream accepted, a stream the input does not have, video, a bitmap subtitle
  aimed at every text extension, the same stream into `.sup` allowed, a text codec on an
  audio stream, and the first problem being the one reported. Plus `ParseProbedStreams`:
  index/kind/codec read by name, an entry without an index skipped, omitted fields filled
  in, and malformed JSON answering nothing rather than throwing.
- `ExtractJobEndpointTests` — validation over the wire: an extraction reaching the engine
  with resolved paths and its metadata, whitespace metadata sending nothing, every codec
  spelling normalized, and refusals for an unsupported codec, `outputPath` alongside
  `outputs`, each encode-only knob, an explicit accelerator (with `auto` and omitted
  accepted), additional inputs, track selection, a default track, audio targets, metadata
  overrides, two outputs at one path, an output equal to the input, a negative index, a
  missing path, and a missing input — plus an engine refusal arriving as the same `400`.
- `ExtractJobPublishTests` — every temp moved onto its output, an existing file replaced,
  a partial publish failing the job while keeping what landed, nothing published when the
  first rename fails, `deleteOutput` removing the whole set, and the descriptor and
  snapshot reporting every output path with the extraction's own name and
  `effectiveHardware`.
