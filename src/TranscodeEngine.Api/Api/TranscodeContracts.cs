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
    int? DefaultSubtitleStreamIndex = null);
