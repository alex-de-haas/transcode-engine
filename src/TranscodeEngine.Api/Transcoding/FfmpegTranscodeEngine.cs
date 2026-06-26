using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// ffmpeg-backed <see cref="ITranscodeEngine"/> and hosted service. Owns a bounded set of worker loops
/// that drain a job queue, spawn ffmpeg per job (VAAPI or software), parse its <c>-progress</c> stream into
/// live snapshots, and raise the start/complete/fail transitions a consumer cares about.
/// </summary>
public sealed class FfmpegTranscodeEngine : ITranscodeEngine, IHostedService, IDisposable
{
    private readonly TranscodeEngineSettings _settings;
    private readonly ILogger<FfmpegTranscodeEngine> _logger;
    private readonly ConcurrentDictionary<string, TranscodeJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    private CancellationTokenSource? _cts;
    private Task[] _workers = [];

    public FfmpegTranscodeEngine(TranscodeEngineSettings settings, ILogger<FfmpegTranscodeEngine> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? JobStarted;
    public event EventHandler<string>? JobCompleted;
    public event EventHandler<string>? JobFailed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settings.AppDataDir);
        foreach (var root in _settings.MediaRoots.Values)
        {
            Directory.CreateDirectory(root);
        }

        _cts = new CancellationTokenSource();
        var workerCount = Math.Max(1, _settings.MaxConcurrentJobs);
        _workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_cts.Token)))
            .ToArray();

        _logger.LogInformation(
            "Transcode engine started with {Workers} worker(s); default hwaccel {Hardware}, vaapi device {Device}.",
            workerCount, _settings.DefaultHardware, _settings.VaapiDevice);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        _cts?.Cancel();

        foreach (var job in _jobs.Values)
        {
            job.CancelRequested = true;
            TryKill(job);
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Best-effort shutdown.
        }
    }

    public async Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken)
    {
        var duration = await ProbeDurationAsync(request.InputPath, cancellationToken);
        var inputSize = TryFileLength(request.InputPath);

        var jobId = Guid.NewGuid().ToString("n");
        var job = new TranscodeJob(jobId, request, duration);
        _jobs[jobId] = job;

        if (!_queue.Writer.TryWrite(jobId))
        {
            _jobs.TryRemove(jobId, out _);
            throw new InvalidOperationException("The transcode engine is shutting down and cannot accept new jobs.");
        }

        return new JobDescriptor(jobId, request.InputPath, request.OutputPath, duration, inputSize);
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.CancelRequested = true;
            TryKill(job);
            // A still-queued job (no process yet) is marked here; the worker skips cancelled jobs.
            if (job.State is JobState.Queued)
            {
                job.Complete(JobState.Cancelled);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken)
    {
        if (_jobs.TryRemove(jobId, out var job))
        {
            job.CancelRequested = true;
            TryKill(job);

            if (deleteOutput)
            {
                TryDeleteOutput(job.Request.OutputPath);
            }
        }

        return Task.CompletedTask;
    }

    public JobSnapshot? GetSnapshot(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;

    public IReadOnlyList<JobSnapshot> GetAllSnapshots() =>
        _jobs.Values.Select(job => job.ToSnapshot()).ToList();

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (!_jobs.TryGetValue(jobId, out var job) || job.CancelRequested)
                {
                    continue;
                }

                await RunJobAsync(job, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunJobAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        // The job can be cancelled between being dequeued and reaching here; don't spawn ffmpeg for it.
        if (job.CancelRequested)
        {
            job.Complete(JobState.Cancelled);
            return;
        }

        var hardware = ResolveHardware(job.Request.HardwareAcceleration);
        job.Start(hardware);
        JobStarted?.Invoke(this, job.JobId);

        var psi = new ProcessStartInfo(_settings.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in BuildArguments(job, hardware))
        {
            psi.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(job.Request.OutputPath) ?? ".");

        var stderrTail = new StderrTail();
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
            process.OutputDataReceived += (_, e) => job.ApplyProgressLine(e.Data);

            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg failed to start.");
            }

            job.AttachProcess(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // A cancel that arrived during start (before the process was attached) would have been a no-op;
            // now that the process is attached, honour it.
            if (job.CancelRequested)
            {
                TryKill(job);
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: the worker token fired. Make sure ffmpeg can't outlive the engine, then stop
                // gracefully — a normal shutdown is not a job failure.
                TryKill(job);
                job.Complete(JobState.Cancelled);
                return;
            }

            if (job.CancelRequested)
            {
                job.Complete(JobState.Cancelled);
                _logger.LogInformation("Job {JobId} cancelled.", job.JobId);
            }
            else if (process.ExitCode == 0)
            {
                job.Complete(JobState.Completed);
                JobCompleted?.Invoke(this, job.JobId);
                _logger.LogInformation("Job {JobId} completed.", job.JobId);
            }
            else
            {
                job.Fail();
                JobFailed?.Invoke(this, job.JobId);
                _logger.LogWarning("Job {JobId} failed (ffmpeg exit {Code}). {Tail}", job.JobId, process.ExitCode, stderrTail.Text);
            }
        }
        catch (Exception exception)
        {
            job.Fail();
            JobFailed?.Invoke(this, job.JobId);
            _logger.LogError(exception, "Job {JobId} errored. {Tail}", job.JobId, stderrTail.Text);
        }
        finally
        {
            // The Process is disposed as the using scope exits; drop the reference so a later cancel/shutdown
            // kill is a no-op instead of touching a disposed object.
            job.DetachProcess();
        }
    }

    /// <summary>Builds the ffmpeg argument list. VAAPI uses the proven software-decode → <c>hwupload</c> →
    /// hardware-encode chain (most compatible across arbitrary inputs); VideoToolbox (native macOS) maps
    /// straight to the platform encoder; software uses libx264/libx265 with an optional CRF.</summary>
    internal List<string> BuildArguments(TranscodeJob job, TranscodeHardware hardware)
    {
        var request = job.Request;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };

        if (hardware == TranscodeHardware.Vaapi)
        {
            args.Add("-vaapi_device");
            args.Add(_settings.VaapiDevice);
        }

        args.Add("-i");
        args.Add(request.InputPath);

        switch (hardware)
        {
            case TranscodeHardware.Vaapi:
                args.Add("-vf");
                args.Add("format=nv12,hwupload");
                args.Add("-c:v");
                args.Add(VaapiEncoder(request.VideoCodec));
                break;
            case TranscodeHardware.VideoToolbox:
                args.Add("-c:v");
                args.Add(VideoToolboxEncoder(request.VideoCodec));
                break;
            default:
                args.Add("-c:v");
                args.Add(SoftwareEncoder(request.VideoCodec));
                if (request.Crf is { } crf)
                {
                    args.Add("-crf");
                    args.Add(crf.ToString(CultureInfo.InvariantCulture));
                }

                break;
        }

        args.Add("-c:a");
        args.Add("copy");
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(request.OutputPath);
        return args;
    }

    /// <summary>Resolves <see cref="TranscodeHardware.Auto"/> to VideoToolbox on a native macOS host, to
    /// VAAPI when a Linux render device is present, otherwise software. An explicit hardware choice the host
    /// cannot satisfy falls back to software with a warning rather than failing the job.</summary>
    internal TranscodeHardware ResolveHardware(TranscodeHardware requested)
    {
        var effective = requested == TranscodeHardware.Auto ? _settings.DefaultHardware : requested;
        if (effective is TranscodeHardware.Auto)
        {
            // The .NET process only reports macOS when running natively on the host (the docker runtime is
            // Linux), which is exactly where VideoToolbox is reachable.
            effective = OperatingSystem.IsMacOS()
                ? TranscodeHardware.VideoToolbox
                : HardwareProbe.Detect(_settings).VaapiAvailable ? TranscodeHardware.Vaapi : TranscodeHardware.None;
        }

        if (effective == TranscodeHardware.Vaapi && !HardwareProbe.Detect(_settings).VaapiAvailable)
        {
            _logger.LogWarning("VAAPI requested but no render device is available; falling back to software.");
            return TranscodeHardware.None;
        }

        if (effective == TranscodeHardware.VideoToolbox && !OperatingSystem.IsMacOS())
        {
            _logger.LogWarning("VideoToolbox requested but the engine is not running natively on macOS; falling back to software.");
            return TranscodeHardware.None;
        }

        return effective;
    }

    private static string VaapiEncoder(TranscodeVideoCodec codec) => codec switch
    {
        TranscodeVideoCodec.H264 => "h264_vaapi",
        TranscodeVideoCodec.Hevc => "hevc_vaapi",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static string VideoToolboxEncoder(TranscodeVideoCodec codec) => codec switch
    {
        TranscodeVideoCodec.H264 => "h264_videotoolbox",
        TranscodeVideoCodec.Hevc => "hevc_videotoolbox",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static string SoftwareEncoder(TranscodeVideoCodec codec) => codec switch
    {
        TranscodeVideoCodec.H264 => "libx264",
        TranscodeVideoCodec.Hevc => "libx265",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private async Task<double?> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(_settings.FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[]
            {
                "-v", "quiet",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath,
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            try
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
            }
            catch (OperationCanceledException)
            {
                // Don't leave ffprobe running when the create request is cancelled.
                TryKillProcess(process);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not probe duration of {Input}; progress will be byte-only.", inputPath);
            return null;
        }
    }

    private static long? TryFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryKill(TranscodeJob job) => TryKillProcess(job.Process);

    private static void TryKillProcess(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            // Process already gone or disposed.
        }
    }

    private void TryDeleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete output {Output}.", outputPath);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        foreach (var job in _jobs.Values)
        {
            job.Process?.Dispose();
        }
    }

    /// <summary>Keeps the last few stderr lines so a failure can be logged with ffmpeg's own message.</summary>
    private sealed class StderrTail
    {
        private const int MaxLines = 20;
        private readonly Queue<string> _lines = new();

        public void Append(string? line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join(" | ", _lines);
                }
            }
        }
    }
}
