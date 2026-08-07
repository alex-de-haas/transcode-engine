using System.Diagnostics;
using System.Globalization;

namespace TranscodeEngine.Api.Transcoding;

/// <summary>Lifecycle state of a transcode job.</summary>
public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// In-memory state of a single transcode job: the request, the running ffmpeg process, and the latest
/// progress parsed from ffmpeg's <c>-progress</c> stream. All mutation goes through a lock so the worker
/// thread and the API/broadcaster threads see a consistent <see cref="ToSnapshot"/>.
/// </summary>
internal sealed class TranscodeJob
{
    private readonly object _gate = new();
    private readonly double? _durationSeconds;

    private JobState _state = JobState.Queued;
    private TranscodeHardware _hardware;
    private double _fps;
    private double _speed;
    private double _outTimeSeconds;
    private long _outputSize;
    private DateTimeOffset? _completedAt;

    // Read without the lock: it is written once, by the worker, before ffmpeg starts, and only read after.
    // Keeping it out of the lock is what lets the measurement below happen outside it.
    private volatile IReadOnlyList<string>? _sizeProbePaths;

    public TranscodeJob(
        string jobId,
        TranscodeJobRequest request,
        double? durationSeconds,
        string? sourcePixelFormat = null)
    {
        JobId = jobId;
        Request = request;
        _durationSeconds = durationSeconds;
        SourcePixelFormat = sourcePixelFormat;
        OutputPaths = request.OutputPaths;
    }

    public string JobId { get; }

    public TranscodeJobRequest Request { get; }

    /// <summary>Every file this job produces — one for a composed job, one per stream for an extraction.
    /// Captured once rather than recomputed, because the snapshot is rebuilt on every progress tick.</summary>
    public IReadOnlyList<string> OutputPaths { get; }

    /// <summary>The primary video stream's ffmpeg pixel format as reported by the create-time ffprobe
    /// (<c>yuv420p10le</c> and friends), or <c>null</c> when the probe could not read one. The encode chain
    /// reads it to size the hardware upload format to the source's bit depth; a null keeps the 8-bit
    /// default.</summary>
    public string? SourcePixelFormat { get; }

    /// <summary>Set true on cancel/remove; the worker checks it after the process exits.</summary>
    public volatile bool CancelRequested;

    /// <summary>The running ffmpeg process, or <c>null</c> before start / after exit.</summary>
    public Process? Process { get; private set; }

    public JobState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>When the job reached a terminal state, used to age it out of retention; <c>null</c> while
    /// queued/running.</summary>
    public DateTimeOffset? CompletedAt
    {
        get { lock (_gate) { return _completedAt; } }
    }

    /// <summary>True once the job has finished (completed, failed, or cancelled).</summary>
    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    public void Start(TranscodeHardware hardware)
    {
        lock (_gate)
        {
            _hardware = hardware;
            _state = JobState.Running;
        }
    }

    public void AttachProcess(Process process) => Process = process;

    /// <summary>
    /// Measures the reported output size by summing these files instead of trusting ffmpeg's
    /// <c>total_size</c>. Set by the worker for a job writing several files, because <c>total_size</c> is
    /// <b>one muxer's</b> byte count, not the run's: a two-output extraction measured here reports only the
    /// first output's size and silently understates the rest. Composed jobs leave this unset and keep
    /// reading <c>total_size</c>, which is exact when there is a single muxer.
    /// </summary>
    public void TrackOutputSizes(IReadOnlyList<string> paths) => _sizeProbePaths = paths;

    /// <summary>Clears the process reference once it has exited and been disposed, so a later
    /// cancel/shutdown kill never touches a disposed <see cref="Process"/>.</summary>
    public void DetachProcess() => Process = null;

    public void Complete(JobState state)
    {
        lock (_gate)
        {
            _state = state;
            _completedAt = DateTimeOffset.UtcNow;
            if (state == JobState.Completed)
            {
                if (_durationSeconds is { } duration && duration > 0)
                {
                    _outTimeSeconds = duration;
                }

                _speed = 0;
                _fps = 0;
            }
        }
    }

    public void Fail()
    {
        lock (_gate)
        {
            _state = JobState.Failed;
            _completedAt = DateTimeOffset.UtcNow;
            _speed = 0;
            _fps = 0;
        }
    }

    /// <summary>Parses one <c>key=value</c> line of ffmpeg's <c>-progress</c> output and folds it into the
    /// live snapshot fields. Unknown keys are ignored.</summary>
    public void ApplyProgressLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        // ffmpeg closes each progress block with a "progress" key, so a tracked job measures its files once
        // per tick. The stat calls happen **outside** the lock on purpose: ToSnapshot takes the same one on
        // every SSE poll and on every cancel, and holding it across per-file I/O would make a slow disk — or
        // simply many outputs — contend with the whole job's state.
        if (key == "progress" && _sizeProbePaths is { Count: > 0 } paths)
        {
            var measured = SumFileLengths(paths);
            lock (_gate)
            {
                _outputSize = measured;
            }

            return;
        }

        lock (_gate)
        {
            switch (key)
            {
                case "out_time_us" or "out_time_ms" when long.TryParse(value, out var micros):
                    // Both keys are microseconds in ffmpeg (out_time_ms is historically mislabeled).
                    _outTimeSeconds = micros / 1_000_000.0;
                    break;
                case "fps" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps):
                    _fps = fps;
                    break;
                // Only trustworthy with a single muxer; a tracked job measures its files instead (above).
                case "total_size" when _sizeProbePaths is null && long.TryParse(value, out var size):
                    _outputSize = size;
                    break;
                case "speed":
                    var trimmed = value.TrimEnd('x', ' ');
                    if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                    {
                        _speed = speed;
                    }

                    break;
            }
        }
    }

    /// <summary>Total bytes on disk across the given files, skipping any that cannot be read — a file the
    /// muxer has not created yet contributes nothing rather than failing the tick.</summary>
    private static long SumFileLengths(IReadOnlyList<string> paths)
    {
        var total = 0L;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Progress reporting must never be the thing that fails a job.
            }
        }

        return total;
    }

    public JobSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            var percent = _durationSeconds is { } duration && duration > 0
                ? Math.Clamp(Math.Round(_outTimeSeconds / duration * 100, 2), 0, 100)
                : (_state is JobState.Completed ? 100 : 0);

            var complete = _state is JobState.Completed;

            double? eta = null;
            if (_state is JobState.Running && _durationSeconds is { } total && total > 0 && _speed > 0)
            {
                eta = Math.Max(0, Math.Round((total - _outTimeSeconds) / _speed, 1));
            }

            // The effective encoder family is only known once the worker has resolved it (after Start);
            // a still-queued job reports null. An extraction reports "none" whatever the worker resolved:
            // it runs no encoder at all, and "software" would claim a software encode that never happened.
            var effectiveHardware = Request.IsExtraction ? "none" : _hardware switch
            {
                TranscodeHardware.Vaapi => "vaapi",
                TranscodeHardware.VideoToolbox => "videotoolbox",
                TranscodeHardware.Amf => "amf",
                TranscodeHardware.None => "software",
                _ => null,
            };

            // An extraction has no single output to name itself after, so it is named for what it reads.
            var name = Request.IsExtraction
                ? Path.GetFileName(Request.InputPath)
                : Request.OutputPath is { } outputPath ? Path.GetFileName(outputPath) : null;

            return new JobSnapshot(
                JobId,
                name,
                effectiveHardware,
                _state.ToString(),
                complete,
                percent,
                Math.Round(_fps, 2),
                Math.Round(_speed, 3),
                _outputSize,
                eta,
                OutputPaths);
        }
    }
}
