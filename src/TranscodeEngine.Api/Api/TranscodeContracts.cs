namespace TranscodeEngine.Api.Api;

/// <summary>
/// Body of <c>POST /jobs</c>. <see cref="InputPath"/> / <see cref="OutputPath"/> are resolved against the
/// media mount selected by their label (by its Hosty label) — required when the engine has more than one
/// media mount, optional when it has exactly one. <see cref="OutputMountLabel"/> defaults to
/// <see cref="InputMountLabel"/> when omitted. <see cref="VideoCodec"/> (<c>h264</c>/<c>hevc</c>),
/// <see cref="HardwareAcceleration"/> (<c>auto</c>/<c>vaapi</c>/<c>videotoolbox</c>/<c>amf</c>/<c>none</c>)
/// and <see cref="Crf"/> fall back to engine defaults when omitted. <see cref="VideoCodec"/> also accepts
/// <c>copy</c> to remux the video untouched — <see cref="MaxHeight"/> and <see cref="Crf"/> are then rejected
/// as contradictory. <see cref="MaxHeight"/> downscales to that height (omit to keep the source resolution).
/// <see cref="AudioStreamIndexes"/>/<see cref="SubtitleStreamIndexes"/> are absolute input stream indices to
/// copy (omit to copy all of that type). <see cref="DefaultAudioStreamIndex"/>/
/// <see cref="DefaultSubtitleStreamIndex"/> mark one mapped track as the default; each requires its matching
/// explicit index list (so the absolute index maps to an output position) and must be a member of it.
/// Subtitle selection/defaults apply only to Matroska (<c>.mkv</c>) outputs.
/// </summary>
public sealed record CreateJobRequest(
    string? InputMountLabel,
    string InputPath,
    string? OutputMountLabel,
    string OutputPath,
    string? VideoCodec,
    string? HardwareAcceleration,
    int? Crf,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null,
    IReadOnlyList<AdditionalInputRequest>? AdditionalInputs = null,
    IReadOnlyList<StreamMetadataOverrideRequest>? MetadataOverrides = null);

/// <summary>
/// A further file whose streams join the output — a sidecar dub or subtitle being merged into the video.
/// Naming any turns the job into a merge, which says nothing about the picture: the video follows
/// <c>videoCodec</c> exactly as it does for any other job, so a merge may re-encode. What a merge changes is
/// the <b>default</b> — omitting <c>videoCodec</c> copies the video, where an ordinary job would encode to
/// HEVC — and the encode-only knobs (<c>maxHeight</c>, <c>crf</c>) are rejected whenever the video ends up
/// copied, whether that was asked for or defaulted to.
/// <para>
/// The path resolves against the media mount its label selects, defaulting to the primary input's mount, and
/// must exist. At least one stream has to be selected, and selections are explicit absolute indexes within
/// that file — the engine turns each into an output position, which it can only do from a known list.
/// </para>
/// </summary>
public sealed record AdditionalInputRequest(
    string? MountLabel,
    string Path,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null);

/// <summary>
/// Replaces one output stream's language and/or title, letting an operator correct a mislabelled track
/// while it is being written. <c>input</c> is the ordinal of the file the stream comes from — 0 is the
/// primary input, 1 the first additional input — and <c>streamIndex</c> its absolute index in that file.
/// Applies to any job, merge or plain transcode. A field left null keeps the source stream's own value.
/// The stream must be one the job explicitly maps, for the same reason a chosen default track must be.
/// </summary>
public sealed record StreamMetadataOverrideRequest(
    int Input,
    int StreamIndex,
    string? Language = null,
    string? Title = null);
