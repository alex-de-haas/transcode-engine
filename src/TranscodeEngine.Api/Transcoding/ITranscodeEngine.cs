namespace TranscodeEngine.Api.Transcoding;

/// <summary>Target video codec for a transcode job.</summary>
public enum TranscodeVideoCodec
{
    H264,
    Hevc,
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
/// preserved) — <see cref="VideoCodec"/>, <see cref="HardwareAcceleration"/>, <see cref="Crf"/> and
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
    int? Crf,
    bool CopyVideo = false,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null);

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
