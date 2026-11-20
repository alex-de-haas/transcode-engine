namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// Maps a <see cref="TranscodeQualityLevel"/> onto each encoder family's own rate control. This table is the
/// reason the preset contract uses levels: a CRF, a QP and a VideoToolbox
/// quality are three different scales, and a job that falls back from one encoder to another has to keep
/// meaning the same thing.
/// <para>
/// <b>Where the numbers come from.</b> The HEVC columns were measured on a 60 s 4K HDR sample cut from a
/// 67.2 Mbps Dolby Vision remux, scored with <c>libvmaf</c> against the source and matched by VMAF rather
/// than by bitrate:
/// </para>
/// <code>
///            libx265 (preset medium)      hevc_amf (-rc cqp)
///   level    CRF   Mbps   VMAF            QP    Mbps   VMAF
///   Highest   18   29.58  91.52           22    32.37  91.41
///   High      20   19.85  89.62           24    18.86  89.13
///   Balanced  22   12.06  87.30           25    ~12    ~87.6  (interpolated)
///   Small     24    6.47  84.63           26     7.91  85.95
/// </code>
/// <para>
/// The two families came out equivalent in quality per byte across that range (±10%, inside the noise of one
/// sample), which is why hardware is a legitimate choice for shrinking a library and not merely the fast one.
/// </para>
/// <para>
/// <b>What is not measured.</b> The H.264 column is derived, not measured — x264 needs a CRF roughly two
/// points lower than x265 for the same picture. The VAAPI and VideoToolbox columns are derived too: VAAPI
/// borrows the AMF QP scale (both are HEVC quantisers), VideoToolbox is interpolated onto its own 1–100
/// quality scale. Both are marked unverified in <c>feature.md</c> until someone runs the same ladder on a
/// Linux render node and on a Mac.
/// </para>
/// </summary>
internal static class QualityLevels
{
    /// <summary>Applied when a request names no level. The measured point where libx265 and AMF come out
    /// equal, so the default behaves the same on every host.</summary>
    internal const TranscodeQualityLevel Default = TranscodeQualityLevel.High;

    internal static string? EncoderQualityError(
        double? encoderQuality,
        bool copiesOrExcludesVideo,
        TranscodeVideoCodec codec,
        TranscodeHardware hardware,
        TranscodeQualityLevel? qualityLevel)
    {
        if (encoderQuality is not { } value) return null;
        if (copiesOrExcludesVideo || hardware == TranscodeHardware.Auto)
            return "encoderQuality requires a video encode with an explicitly selected encoder.";
        if (qualityLevel is not null)
            return "encoderQuality and qualityLevel are mutually exclusive.";
        var option = EncoderOption(codec, hardware);
        if (!double.IsFinite(value) || value < option.Min || value > option.Max)
            return $"encoderQuality ({option.Parameter}) must be between {option.Min} and {option.Max}.";
        if (option.Step == 1 && value != Math.Truncate(value))
            return $"encoderQuality ({option.Parameter}) must be a whole number.";
        if (option.Step == 0.01 && Math.Abs(value - Math.Round(value, 2)) > 1e-9)
            return $"encoderQuality ({option.Parameter}) must have at most two decimal places.";
        return null;
    }

    internal static EncoderQualityOption EncoderOption(TranscodeVideoCodec codec, TranscodeHardware hardware)
    {
        var videoCodec = codec == TranscodeVideoCodec.H264 ? "h264" : "hevc";
        return hardware switch
        {
            TranscodeHardware.None => new(videoCodec, "none", "crf", 0, 51, 0.01, SoftwareCrf(Default, codec)),
            // FFmpeg treats zero as an omitted explicit VAAPI QP.
            TranscodeHardware.Vaapi => new(videoCodec, "vaapi", "qp", 1, 51, 1, HardwareQp(Default, codec)),
            TranscodeHardware.Amf => new(videoCodec, "amf", "qp", 0, 51, 1, HardwareQp(Default, codec)),
            TranscodeHardware.VideoToolbox => new(videoCodec, "videotoolbox", "q:v", 1, 100, 1, VideoToolboxQuality(Default, codec)),
            _ => throw new ArgumentOutOfRangeException(nameof(hardware)),
        };
    }

    /// <summary>CRF presets for <c>libx264</c> and <c>libx265</c>. Lower requests more detail.
    /// The H.264 presets use CRFs two points below the HEVC presets.</summary>
    internal static int SoftwareCrf(TranscodeQualityLevel level, TranscodeVideoCodec codec) =>
        (level, codec) switch
        {
            (TranscodeQualityLevel.Highest, TranscodeVideoCodec.Hevc) => 18,
            (TranscodeQualityLevel.High, TranscodeVideoCodec.Hevc) => 20,
            (TranscodeQualityLevel.Balanced, TranscodeVideoCodec.Hevc) => 22,
            (TranscodeQualityLevel.Small, TranscodeVideoCodec.Hevc) => 24,
            (TranscodeQualityLevel.Highest, _) => 16,
            (TranscodeQualityLevel.High, _) => 18,
            (TranscodeQualityLevel.Balanced, _) => 20,
            _ => 22,
        };

    /// <summary>Constant quantiser for the QP-based hardware encoders (AMF, VAAPI). AMF offers nothing else:
    /// <c>qvbr</c>, <c>hqvbr</c>, <c>vbr_peak</c> with VBAQ and CQP with pre-analysis all fail encoder init
    /// with <c>AMF_NOT_SUPPORTED</c> on the hardware this was measured against, so CQP is the only knob
    /// there — and it is enough.</summary>
    internal static int HardwareQp(TranscodeQualityLevel level, TranscodeVideoCodec codec) =>
        (level, codec) switch
        {
            (TranscodeQualityLevel.Highest, TranscodeVideoCodec.Hevc) => 22,
            (TranscodeQualityLevel.High, TranscodeVideoCodec.Hevc) => 24,
            (TranscodeQualityLevel.Balanced, TranscodeVideoCodec.Hevc) => 25,
            (TranscodeQualityLevel.Small, TranscodeVideoCodec.Hevc) => 26,
            (TranscodeQualityLevel.Highest, _) => 20,
            (TranscodeQualityLevel.High, _) => 22,
            (TranscodeQualityLevel.Balanced, _) => 23,
            _ => 24,
        };

    /// <summary>VideoToolbox's <c>-q:v</c>, a 1–100 scale where <b>higher is better</b> — the opposite
    /// direction to every other column here.</summary>
    internal static int VideoToolboxQuality(TranscodeQualityLevel level, TranscodeVideoCodec codec) =>
        (level, codec) switch
        {
            (TranscodeQualityLevel.Highest, TranscodeVideoCodec.Hevc) => 70,
            (TranscodeQualityLevel.High, TranscodeVideoCodec.Hevc) => 62,
            (TranscodeQualityLevel.Balanced, TranscodeVideoCodec.Hevc) => 55,
            (TranscodeQualityLevel.Small, TranscodeVideoCodec.Hevc) => 48,
            (TranscodeQualityLevel.Highest, _) => 75,
            (TranscodeQualityLevel.High, _) => 67,
            (TranscodeQualityLevel.Balanced, _) => 60,
            _ => 53,
        };

    /// <summary>Parses the wire spelling of a level. Returns false for anything unrecognised so the endpoint
    /// can name the accepted set, rather than silently encoding at the default.</summary>
    internal static bool TryParse(string? raw, out TranscodeQualityLevel level)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "highest":
                level = TranscodeQualityLevel.Highest;
                return true;
            case "high":
                level = TranscodeQualityLevel.High;
                return true;
            case "balanced":
                level = TranscodeQualityLevel.Balanced;
                return true;
            case "small":
                level = TranscodeQualityLevel.Small;
                return true;
            default:
                level = default;
                return false;
        }
    }

    /// <summary>The accepted spellings, for error messages.</summary>
    internal static string Accepted => "'highest', 'high', 'balanced' or 'small'";
}
