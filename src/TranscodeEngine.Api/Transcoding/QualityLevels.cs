namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// Maps a <see cref="TranscodeQualityLevel"/> onto each encoder family's own rate control. This table is the
/// whole reason the contract talks about levels instead of CRF numbers: a CRF, a QP and a VideoToolbox
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

    /// <summary>CRF for <c>libx264</c>/<c>libx265</c>. Lower is better; the H.264 values sit two points
    /// below their HEVC counterparts because x264 needs the extra quality for the same result.</summary>
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
