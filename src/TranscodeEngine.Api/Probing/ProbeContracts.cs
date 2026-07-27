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
    int? Width,
    int? Height,
    double? FrameRate,
    int? BitDepth,
    HdrFormat Hdr,
    int? Channels,
    int? SampleRate);

/// <summary>A probed file: its container, overall figures, and every stream in ffprobe's order.</summary>
public sealed record ProbeResponse(
    string Container,
    double? DurationSeconds,
    int? Bitrate,
    long SizeBytes,
    IReadOnlyList<ProbedStreamInfo> Streams);
