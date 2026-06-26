namespace TranscodeEngine.Api.Api;

/// <summary>
/// Body of <c>POST /jobs</c>. <see cref="InputPath"/> / <see cref="OutputPath"/> are resolved against the
/// media mount selected by their label (by its Hosty label) — required when the engine has more than one
/// media mount, optional when it has exactly one. <see cref="OutputMountLabel"/> defaults to
/// <see cref="InputMountLabel"/> when omitted. <see cref="VideoCodec"/> (<c>h264</c>/<c>hevc</c>),
/// <see cref="HardwareAcceleration"/> (<c>auto</c>/<c>vaapi</c>/<c>none</c>) and <see cref="Crf"/> fall back
/// to engine defaults when omitted.
/// </summary>
public sealed record CreateJobRequest(
    string? InputMountLabel,
    string InputPath,
    string? OutputMountLabel,
    string OutputPath,
    string? VideoCodec,
    string? HardwareAcceleration,
    int? Crf);
