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

    public TranscodeJob(string jobId, TranscodeJobRequest request, double? durationSeconds)
    {
        JobId = jobId;
        Request = request;
        _durationSeconds = durationSeconds;
    }

    public string JobId { get; }

    public TranscodeJobRequest Request { get; }

    /// <summary>Set true on cancel/remove; the worker checks it after the process exits.</summary>
    public volatile bool CancelRequested;

    /// <summary>The running ffmpeg process, or <c>null</c> before start / after exit.</summary>
    public Process? Process { get; private set; }

    public JobState State
    {
        get { lock (_gate) { return _state; } }
    }

    public void Start(TranscodeHardware hardware)
    {
        lock (_gate)
        {
            _hardware = hardware;
            _state = JobState.Running;
        }
    }

    public void AttachProcess(Process process) => Process = process;

    /// <summary>Clears the process reference once it has exited and been disposed, so a later
    /// cancel/shutdown kill never touches a disposed <see cref="Process"/>.</summary>
    public void DetachProcess() => Process = null;

    public void Complete(JobState state)
    {
        lock (_gate)
        {
            _state = state;
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
                case "total_size" when long.TryParse(value, out var size):
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
            // a still-queued job reports null.
            var effectiveHardware = _hardware switch
            {
                TranscodeHardware.Vaapi => "vaapi",
                TranscodeHardware.VideoToolbox => "videotoolbox",
                TranscodeHardware.None => "software",
                _ => null,
            };

            return new JobSnapshot(
                JobId,
                Path.GetFileName(Request.OutputPath),
                effectiveHardware,
                _state.ToString(),
                complete,
                percent,
                Math.Round(_fps, 2),
                Math.Round(_speed, 3),
                _outputSize,
                eta);
        }
    }
}
