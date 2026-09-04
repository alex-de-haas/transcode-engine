using System.Text.Json.Serialization;

namespace TranscodeEngine.Api.Probing;

/// <summary>
/// Body of <c>POST /probe</c>. The path is resolved against the media mount its label selects, exactly as
/// <c>POST /jobs</c> resolves its input — required when the engine has more than one media mount, optional
/// when it has exactly one.
/// </summary>
public sealed record ProbeRequest(string? MountLabel, string Path);

/// <summary>The kind of a stream, in the vocabulary this API owns rather than ffprobe's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProbedStreamKind>))]
public enum ProbedStreamKind
{
    Video,
    Audio,
    Subtitle,
    /// <summary>Data, attachment, or anything else a consumer does not model; kept so stream indexes stay
    /// contiguous and comparable with ffprobe's own numbering.</summary>
    Other,
}

/// <summary>
/// How a video stream carries brightness. Distinct from a nullable field on purpose: <see cref="Unknown"/>
/// means nobody could tell, <see cref="Sdr"/> means the file says it is not HDR, and collapsing the two
/// would let a consumer's weaker fallback provider assert something it never determined. This app answers
/// from ffprobe and should never need <see cref="Unknown"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HdrFormat>))]
public enum HdrFormat
{
    Unknown,
    Sdr,
    /// <summary>PQ (SMPTE ST 2084) without a recognised dynamic-metadata layer.</summary>
    Hdr10,
    /// <summary>PQ with SMPTE 2094-40 dynamic metadata. Only reported when the container signals it at
    /// stream level; a file whose HDR10+ layer lives purely in frame side data reports
    /// <see cref="Hdr10"/> instead, which under-reports rather than inventing.</summary>
    Hdr10Plus,
    /// <summary>Hybrid Log-Gamma (ARIB STD-B67).</summary>
    Hlg,
    /// <summary>A Dolby Vision configuration record is present on the stream.</summary>
    DolbyVision,
}

/// <summary>
/// The Dolby Vision configuration record a video stream carries — the same 24 bytes the container holds
/// in an MP4 <c>dvcC</c>/<c>dvvC</c> box or a Matroska <c>BlockAdditionMapping</c>, read through ffprobe's
/// <c>side_data_list</c>. It is what tells profile 7 (dual layer, from disc; Apple hardware plays its HDR10
/// base layer) from profile 8.1 (single layer; plays as Dolby Vision), which <see cref="HdrFormat.DolbyVision"/>
/// alone cannot.
/// </summary>
/// <param name="Profile">The Dolby Vision profile: 5, 7 or 8 in practice.</param>
/// <param name="Level">The Dolby Vision level (6 is 4K at 24 fps and the common value for a film).</param>
/// <param name="BlSignalCompatibilityId">What the base layer is on its own: 1 is HDR10 (profile 8.1), 2 SDR
/// (8.2), 4 HLG (8.4), 6 the HDR10 a UHD Blu-ray carries under profile 7, 0 none (profile 5).</param>
/// <param name="RpuPresent">Whether RPU metadata is present.</param>
/// <param name="ElPresent">Whether an enhancement layer is present — the mark of a dual-layer profile 7.</param>
/// <param name="BlPresent">Whether a base layer is present.</param>
public sealed record DolbyVisionInfo(
    int Profile,
    int Level,
    int BlSignalCompatibilityId,
    bool RpuPresent,
    bool ElPresent,
    bool BlPresent);

/// <summary>
/// One stream of a probed file. <see cref="Index"/> is ffprobe's absolute stream index, including the
/// entry it synthesizes for embedded cover art — job creation addresses streams by that index, so a
/// consumer mixing this with its own parser must see the same numbering.
/// </summary>
public sealed record ProbedStreamInfo(
    int Index,
    ProbedStreamKind Kind,
    string? Codec,
    string? Profile,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    /// <summary>This stream's own bitrate in bits per second, or null when the file does not state one.
    /// Never derived from the file's overall rate: a consumer sizing one track needs a figure it can stand
    /// behind, and a share of the whole is a guess.</summary>
    int? Bitrate,
    int? Width,
    int? Height,
    double? FrameRate,
    int? BitDepth,
    HdrFormat Hdr,
    int? Channels,
    int? SampleRate,
    /// <summary>The Dolby Vision configuration record, when the stream carries one; null otherwise, and
    /// always null for anything but video.</summary>
    DolbyVisionInfo? DolbyVision = null);

/// <summary>A probed file: its container, overall figures, and every stream in ffprobe's order.</summary>
public sealed record ProbeResponse(
    string Container,
    double? DurationSeconds,
    int? Bitrate,
    long SizeBytes,
    IReadOnlyList<ProbedStreamInfo> Streams);
