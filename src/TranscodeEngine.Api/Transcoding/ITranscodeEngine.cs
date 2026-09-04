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

/// <summary>
/// What happens to a source's Dolby Vision when the picture is copied. <see cref="Keep"/> leaves the stream
/// exactly as it is. <see cref="ToProfile81"/> rewrites a dual-layer profile 7 source — every UHD Blu-ray
/// remux — into single-layer profile 8.1: the RPU metadata is rewritten, the enhancement layer is dropped,
/// and the HEVC picture is copied byte for byte. Profile 7 is the one form of Dolby Vision that Apple TV and
/// Infuse cannot decode and quietly play as HDR10; profile 8.1 is the form they play as Dolby Vision. Only
/// meaningful on a video copy: a re-encode drops Dolby Vision whatever is asked.
/// </summary>
public enum DolbyVisionMode
{
    Keep,
    ToProfile81,
}

/// <summary>What an extracted stream is written as. <see cref="Copy"/> is the whole point — an extraction
/// takes the packets as they are — and the text targets exist for the one case that cannot: a subtitle codec
/// with no file form of its own (notably <c>mov_text</c>) has to become one to be extracted at all. There is
/// deliberately no audio target here: re-encoding on the way out would make this a second, worse encoder
/// surface, and <see cref="AudioTarget"/> already serves a composed output.</summary>
public enum ExtractionCodec
{
    Copy,
    Srt,
    Ass,
    WebVtt,
}

/// <summary>
/// One stream of the primary input written out as its own file. <see cref="Path"/> is already resolved
/// against a media mount, and <see cref="StreamIndex"/> is a single absolute index in the input — not a list,
/// which is the design rather than a limitation: one stream per file is what makes each output addressable as
/// itself, so <see cref="Language"/> and <see cref="Title"/> sit here instead of going through a
/// <see cref="StreamMetadataOverride"/>. An override's (input, streamIndex) pair exists to locate a stream's
/// position in a composed output; here position 0 of its own file is the only place the stream can be.
/// A null language or title leaves whatever the source stream carries.
/// </summary>
public sealed record ExtractionOutput(
    string Path,
    int StreamIndex,
    ExtractionCodec Codec = ExtractionCodec.Copy,
    string? Language = null,
    string? Title = null);

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
/// </para>
/// <para>
/// <see cref="DolbyVision"/> asks for the picture's Dolby Vision to be rewritten to profile 8.1 while it is
/// copied (<see cref="DolbyVisionMode"/>); the endpoint refuses it with anything but a video copy.
/// </para>
/// <para>
/// <see cref="Outputs"/> is the other shape this request takes: naming any makes the job an <b>extraction</b>,
/// which writes each named stream to its own file and produces no composed output at all. Every field above
/// describes a composed output and is refused alongside it — there is no picture in an extraction to encode,
/// scale or accelerate. <see cref="OutputPath"/> is null exactly then.
/// </para></summary>
public sealed record TranscodeJobRequest(
    string InputPath,
    string? OutputPath,
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
    IReadOnlyList<AudioTarget>? AudioTargets = null,
    IReadOnlyList<ExtractionOutput>? Outputs = null,
    DolbyVisionMode DolbyVision = DolbyVisionMode.Keep)
{
    /// <summary>Whether this job writes its input's streams out as separate files rather than composing one.</summary>
    public bool IsExtraction => Outputs is { Count: > 0 };

    /// <summary>Whether this job rewrites the picture's Dolby Vision to profile 8.1 on its way through. Such
    /// a job runs as several tool stages rather than one ffmpeg invocation — see
    /// <c>FfmpegTranscodeEngine.DolbyVision.cs</c>.</summary>
    public bool ConvertsDolbyVision => DolbyVision == DolbyVisionMode.ToProfile81;

    /// <summary>Every file this job produces, in the order it declares them. One entry for a composed job,
    /// one per stream for an extraction — the single list everything downstream (publishing, deletion,
    /// snapshots) works from, so neither shape needs a special case of its own.</summary>
    public IReadOnlyList<string> OutputPaths =>
        Outputs is { Count: > 0 } outputs ? outputs.Select(output => output.Path).ToList()
        : OutputPath is { Length: > 0 } path ? [path]
        : [];
}

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

/// <summary>What is known about a job right after it is created (before the worker picks it up).
/// <see cref="OutputPath"/> is the composed output and is null for an extraction, which has none;
/// <see cref="OutputPaths"/> lists every file the job will produce and is the field to read when either
/// shape is possible.</summary>
public sealed record JobDescriptor(
    string JobId,
    string InputPath,
    string? OutputPath,
    double? DurationSeconds,
    long? InputSizeBytes,
    IReadOnlyList<string>? OutputPaths = null);

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
    double? EtaSeconds,
    IReadOnlyList<string>? OutputPaths = null);

/// <summary>
/// Thin wrapper over ffmpeg. Owns no persistence; surfaces live snapshots and raises events for the
/// transitions a consumer cares about. The control API exposes these over HTTP/SSE.
/// </summary>
public interface ITranscodeEngine
{
    /// <summary>Creates a job (probes the input for its duration, enqueues it, returns the descriptor).
    /// The job runs as soon as a worker is free.
    /// <para>
    /// Throws <see cref="ArgumentException"/> when an extraction names a stream the input does not have, or
    /// one whose codec the requested file cannot carry. That check lives here rather than in the endpoint
    /// because it needs the input's streams, and this is where the input is already probed — and it is worth
    /// paying for: an extraction is disk-bound, so a typo discovered by ffmpeg is a typo discovered after
    /// reading the whole container.
    /// </para></summary>
    Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a running or queued job (kills the ffmpeg process if it is running).</summary>
    Task CancelAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Forgets a job and, when <paramref name="deleteOutput"/> is set, deletes its (partial)
    /// output files — every one of them, since an extraction's outputs are one job's result and not several.</summary>
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
