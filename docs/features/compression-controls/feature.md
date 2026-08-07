# Compression Controls

Created: 2026-08-06
Updated: 2026-08-07

The two knobs that let a job make a file **smaller** rather than merely different: a
quality level that every encoder family honours, and per-track audio re-encoding.

Before these, a job could drop tracks and re-encode the picture, but the only rate
control it applied was `-crf` on the software path. VAAPI, AMF and VideoToolbox ran
at whatever their driver defaulted to, so on a hardware host there was no way to ask
for less; and audio was always `-c:a copy`, which on a real library file leaves the
larger half untouched.

## Quality level, not CRF

`POST /jobs` takes `qualityLevel` — `highest`, `high`, `balanced` or `small` —
defaulting to `high`. It is deliberately not a CRF: a CRF, a constant quantiser and
VideoToolbox's quality scale are three different numbers, and the engine's hardware
selection is opportunistic. A job that falls back from `hevc_amf` to `libx265`
because the host has no AMD driver must keep asking for the same picture; a raw CRF
forwarded to whichever encoder answered could not.

`QualityLevels` owns the translation and `AddVideoEncode` emits it:

| Family | Arguments | Level → value (HEVC) |
| --- | --- | --- |
| Software (`libx265`/`libx264`) | `-crf N` | 18 / 20 / 22 / 24 |
| AMF (`hevc_amf`) | `-rc cqp -qp_i N -qp_p N` | 22 / 24 / 25 / 26 |
| VAAPI (`hevc_vaapi`) | `-rc_mode CQP -qp N` | 22 / 24 / 25 / 26 |
| VideoToolbox | `-q:v N` | 70 / 62 / 55 / 48 |

VideoToolbox counts the other way — higher is better — which is exactly why the
contract does not expose the number. H.264 asks for a CRF two points lower than HEVC
at the same level, since x264 needs the extra quality to match.

A family that cannot honour a level **fails the job**. VideoToolbox's constant-quality
mode does not exist on every host (notably Intel Macs); there ffmpeg cannot open the
encoder and the job carries its message. Falling back to the driver default would
hand back a file the requested level says nothing about.

### Where the numbers come from

Measured on a 60 s 4K HDR sample cut from a 67.2 Mbps Dolby Vision remux, scored with
`libvmaf` against the source and matched **by VMAF, not by bitrate**:

| Encoder | Setting | Mbps | VMAF |
| --- | --- | ---: | ---: |
| libx265 (preset medium) | CRF 18 | 29.58 | 91.52 |
| libx265 | CRF 20 | 19.85 | 89.62 |
| libx265 | CRF 22 | 12.06 | 87.30 |
| libx265 | CRF 24 | 6.47 | 84.63 |
| hevc_amf | CQP 22 | 32.37 | 91.41 |
| hevc_amf | CQP 24 | 18.86 | 89.13 |
| hevc_amf | CQP 26 | 7.91 | 85.95 |
| hevc_amf | CQP 28 | 3.73 | 83.04 |
| hevc_amf | CQP 30 | 2.13 | 80.43 |

The two families came out **equivalent in quality per byte** across that range (±10%,
inside the noise of a single sample) at a 30–70× speed difference — which is why
hardware is a legitimate choice for shrinking a library and not merely the fast one,
and why `high` is the default: it is the measured point where they meet.

Two facts about AMF that shaped the arguments. `qvbr`, `hqvbr`, `vbr_peak` with VBAQ,
and CQP with pre-analysis all fail encoder init with `AMF_NOT_SUPPORTED`, so CQP is
the only quality-style mode available; and `-quality quality` changes nothing
measurable (identical size and VMAF at QP 22 and QP 26), so the engine does not set
it.

Unverified: the VAAPI column borrows the AMF quantiser scale and the VideoToolbox
column is interpolated. Neither has been measured on its own hardware.

Caveat on the table itself: `vmaf_v0.6.1` is calibrated for 1080p SDR viewing, and
the whole 2–32 Mbps range compresses into 80–92 points. It ranks encoders against
each other reliably. It cannot say which level is visually good enough.

## Per-track audio re-encoding

`audioTargets` re-encodes chosen audio tracks while the rest are copied. Each entry
addresses a track the way a metadata override does — `input` plus `streamIndex` —
names a `codec` (`eac3` or `ac3`), and may set a `bitrate` in kbps.

Per track rather than per job, because one file's tracks want opposite answers. The
file this exists for holds nineteen voice-over dubs stored as lossless DTS-HD MA 7.1
(~4.1 GB each) beside an original TrueHD Atmos track: the dubs are pure waste at
5 Mbps, the original must not be touched.

Naming a target requires an explicit `audioStreamIndexes`, the same requirement a
chosen default track carries — `-c:a:N` needs an output position, and a "copy every
audio stream" mapping expands to however many the file holds. With no targets at all
the job keeps emitting the single blanket `-c:a copy`, which is the only form that
works alongside that mapping.

Two things the engine deliberately does **not** do:

- **No channel argument.** The encoders advertise the layouts they accept and ffmpeg
  negotiates a downmix into the filter graph, so a 7.1 source lands as 5.1 on its
  own. Forcing `-ac` would also upmix a stereo track.
- **No bitrate default of its own.** Omit `bitrate` and ffmpeg scales one to the
  channel count (448k for 5.1, 192k stereo, 96k mono). A single engine-side number
  would either starve a 5.1 dub or waste bits on a mono commentary.

Audio targets are independent of what happens to the picture, so the cheapest useful
conversion — shrink the audio, copy every frame of video — is one job.

## Dolby Vision does not survive a re-encode

A re-encoded job writes no Dolby Vision. What survives is the base layer, and on
profiles 7 and 8 that base layer is ordinary HEVC Main 10 PQ — a valid HDR10 picture,
not a broken one. HDR10's static metadata rides through: an AMF re-encode of a Dolby
Vision source was checked and carries its mastering-display primaries and
`MaxCLL 468 / MaxFALL 201` on the output.

**Profile 5 is the exception.** Its base layer is IPT-PQ-c2 and is not viewable
without the RPU — dropping the layer there does not degrade the picture, it wrecks the
colours. The engine does not distinguish profiles today; a consumer holding profile 5
material should copy the video rather than encode it.

This is a settled boundary rather than pending work, and two measurements settle it.

**Preserving the layer would cost the pipeline and buy no bytes.** No hardware encoder
can emit an RPU, so a Dolby Vision job is x265-only — and per the table above x265 and
AMF are equivalent in quality per byte. The same file size would take 30–70× longer
(a 107-minute feature: ~28 minutes on AMF against a day or more on x265), and the only
thing bought is the dynamic metadata. It would also cost the engine's shape: a job is
one ffmpeg process whose `-progress` stream drives both the snapshot and the
five-minute no-progress watchdog, while Dolby Vision needs three stages (extract the
RPU, encode, mux with `mkvmerge` so the container carries the DV signalling), two of
which emit no progress at all while moving tens of gigabytes.

**Discarding the enhancement layer is not worth a feature either.** A profile 7 source
carries a second layer that `dovi_tool` can drop losslessly, converting to profile 8.1
without re-encoding a frame — which sounded like the cheap win. Measured on a 300 s
sample of the same remux: base layer 1732.1 MB, enhancement layer 75.2 MB. That is
**4.2% of the video stream and 1.6% of the file** — a minimal enhancement layer, where
a full one would be 10–25%. Extrapolated over the feature, ~2.3 GB out of 141.7 GB.

What actually shrinks such a file is its audio: in that same 141.7 GB remux, 87.5 GB
is audio and 54.1 GB is video. See [Per-track audio re-encoding](#per-track-audio-re-encoding).

## Testing Expectations

- The level → argument mapping is pinned per family, including VideoToolbox's
  inverted scale and H.264's lower CRF; an omitted level must resolve to the default.
- A copied video carries no rate control.
- With no audio targets the blanket `-c:a copy` survives; with one target, that track
  re-encodes and every other mapped track is still copied.
- An omitted bitrate emits no `-b:a:N`, and no `-ac` is ever emitted.
- Endpoint validation: unknown quality level, unknown audio codec, a target for an
  unmapped stream, a target without an explicit selection, duplicate targets, and a
  quality level combined with a copied video.
