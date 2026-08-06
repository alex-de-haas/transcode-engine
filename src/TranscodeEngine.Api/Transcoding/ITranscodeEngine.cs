namespace TranscodeEngine.Api.Transcoding;

/// <summary>Target video codec for a transcode job.</summary>
public enum TranscodeVideoCodec
{
    H264,
    Hevc,
}

/// <summary>
/// How much picture a re-encode is allowed to spend bits on. Encoder-independent by design: the engine owns
/// the mapping onto each family's own rate control (<c>QualityLevels</c>), so the same level means the same
/// picture whether the job lands on libx265 or on a hardware encoder. That is what makes the opportunistic
/// hardware fallback honest — a job that silently moves to another encoder must not silently change quality
/// with it.
/// </summary>
public enum TranscodeQualityLevel
{
    /// <summary>Visually near-transparent; roughly halves a UHD remux.</summary>
    Highest,

    /// <summary>The default. Around a 3.5× reduction on a UHD remux.</summary>
    High,

    /// <summary>Noticeably smaller, still comfortable on a large screen.</summary>
    Balanced,

    /// <summary>Size first; compression artefacts become findable.</summary>
    Small,
}

/// <summary>What a kept audio track is re-encoded to. Lossy only, by design: the point of re-encoding audio
/// at all is size, and a lossless target would spend the job's time to save nothing.</summary>
public enum TranscodeAudioCodec
{
    /// <summary>Dolby Digital Plus. The default choice — it carries 5.1 at a fraction of a lossless
    /// track's bitrate and every player this app's outputs reach can decode it.</summary>
    Eac3,

    /// <summary>Dolby Digital. Lower ceiling than E-AC-3, for the rare device that stops at AC-3.</summary>
    Ac3,
}

/// <summary>Which encoder family a job runs on. <see cref="Auto"/> picks VideoToolbox on a native macOS
/// host, AMF on a native Windows host with an AMD driver, VAAPI when a Linux render device is present, and
/// falls back to software otherwise.</summary>
public enum TranscodeHardware
{
    Auto,
    Vaapi,
    VideoToolbox,
    Amf,
    None,
}

/// <summary>A resolved transcode request: absolute input/output paths (already validated against the
/// media mounts by the endpoint) plus the encode parameters. The engine owns no path resolution.
/// <para>
/// <see cref="CopyVideo"/> remuxes the video stream untouched (no re-encode, lossless, HDR/Dolby Vision
/// preserved) — <see cref="VideoCodec"/>, <see cref="HardwareAcceleration"/>, <see cref="QualityLevel"/> and
/// <see cref="MaxHeight"/> are then irrelevant. <see cref="MaxHeight"/> downscales to that height
/// (aspect kept, never upscales — the caller is expected to omit it when the source is already smaller).
/// <see cref="AudioStreamIndexes"/>/<see cref="SubtitleStreamIndexes"/> are absolute input stream indices
/// to copy, in output order; <c>null</c> copies all of that type. <see cref="DefaultAudioStreamIndex"/>/
/// <see cref="DefaultSubtitleStreamIndex"/> mark one mapped track as the container default (requires the
/// matching explicit index list); <c>null</c> keeps the source dispositions.
/// </para></summary>
public sealed record TranscodeJobRequest(
    string InputPath,
    string OutputPath,
    TranscodeVideoCodec VideoCodec,
    TranscodeHardware HardwareAcceleration,
    TranscodeQualityLevel? QualityLevel,
    bool CopyVideo = false,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null,
    IReadOnlyList<AdditionalInput>? AdditionalInputs = null,
    IReadOnlyList<StreamMetadataOverride>? MetadataOverrides = null,
    IReadOnlyList<AudioTarget>? AudioTargets = null);

/// <summary>
/// Re-encodes one mapped audio track instead of copying it. Addressed the same way a metadata override is —
/// by (<see cref="Input"/>, <see cref="StreamIndex"/>) — because it answers the same question: which output
/// position does this argument belong to.
/// <para>
/// Per track rather than per job, because one file's tracks want opposite answers: a lossless 7.1 voice-over
/// dub is pure waste at 5 Mbps, while the original Atmos track beside it must not be touched.
/// </para>
/// <para>
/// <see cref="BitrateKbps"/> is optional. Omitted, ffmpeg picks a default that scales with the channel count
/// (448k for 5.1, 192k for stereo, 96k for mono) — sane, if conservative for a library. Channel count needs
/// no handling here at all: the encoders advertise what they accept and ffmpeg downmixes to fit, so a 7.1
/// source becomes 5.1 without being asked.
/// </para>
/// </summary>
public sealed record AudioTarget(
    int Input,
    int StreamIndex,
    TranscodeAudioCodec Codec,
    int? BitrateKbps = null);

/// <summary>
/// A file whose streams join the output alongside the primary input's — a sidecar dub or subtitle being
/// merged in. Its selections are absolute stream indexes within <b>that</b> file and are always explicit:
/// the engine turns each into an output position for the metadata and disposition arguments, which it can
/// only do from a known list. <see cref="Path"/> is already resolved against a media mount.
/// </summary>
public sealed record AdditionalInput(
    string Path,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null);

/// <summary>
/// Replaces one output stream's language and/or title. <see cref="Input"/> is the ordinal of the file the
/// stream comes from — 0 is the primary input, 1 the first <see cref="AdditionalInput"/> — and
/// <see cref="StreamIndex"/> is its absolute index within that file. A null field leaves the source
/// stream's own value alone, so editing one track never freezes the others' metadata.
/// </summary>
public sealed record StreamMetadataOverride(
    int Input,
    int StreamIndex,
    string? Language = null,
    string? Title = null);

/// <summary>What is known about a job right after it is created (before the worker picks it up).</summary>
public sealed record JobDescriptor(
    string JobId,
    string InputPath,
    string OutputPath,
    double? DurationSeconds,
    long? InputSizeBytes);

/// <summary>A live, in-memory progress snapshot (never persisted). <see cref="EffectiveHardware"/> is the
/// encoder family actually selected after auto-detect/fallback (<c>vaapi</c> / <c>videotoolbox</c> /
/// <c>amf</c> / <c>software</c>; <c>null</c> while queued), so a consumer can tell whether hardware encoding
/// is in effect.</summary>
public sealed record JobSnapshot(
    string JobId,
    string? Name,
    string? EffectiveHardware,
    string State,
    bool Complete,
    double PercentComplete,
    double Fps,
    double Speed,
    long OutputSizeBytes,
    double? EtaSeconds);

/// <summary>
/// Thin wrapper over ffmpeg. Owns no persistence; surfaces live snapshots and raises events for the
/// transitions a consumer cares about. The control API exposes these over HTTP/SSE.
/// </summary>
public interface ITranscodeEngine
{
    /// <summary>Creates a job (probes the input for its duration, enqueues it, returns the descriptor).
    /// The job runs as soon as a worker is free.</summary>
    Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a running or queued job (kills the ffmpeg process if it is running).</summary>
    Task CancelAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Forgets a job and, when <paramref name="deleteOutput"/> is set, deletes its (partial)
    /// output file.</summary>
    Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken);

    JobSnapshot? GetSnapshot(string jobId);

    IReadOnlyList<JobSnapshot> GetAllSnapshots();

    /// <summary>Raised when a job transitions from queued to running.</summary>
    event EventHandler<string>? JobStarted;

    /// <summary>Raised when a job finishes successfully.</summary>
    event EventHandler<string>? JobCompleted;

    /// <summary>Raised when a job fails (non-zero ffmpeg exit) or errors.</summary>
    event EventHandler<string>? JobFailed;
}
