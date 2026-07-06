using System.Collections.Concurrent;
using System.ComponentModel;
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
    // A job is killed if ffmpeg emits no -progress line for this long: a hung VAAPI init, a stalled NFS
    // read, or a special file (FIFO) that never returns would otherwise block its worker — and, at the
    // default MAX_CONCURRENT_JOBS=1, the whole engine — indefinitely.
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromMinutes(5);

    // Hard cap on the ffprobe duration probe so the same kind of special/blocked file can't hang CreateAsync.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // Terminal jobs are kept this long (and at most this many) so a late GET /jobs poll still sees them,
    // then evicted — otherwise the in-memory dictionary (and the SSE snapshot list) grows without bound.
    private static readonly TimeSpan TerminalJobRetention = TimeSpan.FromHours(1);
    private const int MaxRetainedTerminalJobs = 500;

    // The queue is bounded so a flood of POST /jobs can't grow it (and the job dictionary) without limit;
    // a full queue is surfaced to the caller instead of silently consuming memory.
    private const int MaxQueuedJobs = 1024;

    private readonly TranscodeEngineSettings _settings;
    private readonly ILogger<FfmpegTranscodeEngine> _logger;
    private readonly ConcurrentDictionary<string, TranscodeJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedJobs) { FullMode = BoundedChannelFullMode.Wait });

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
            throw new InvalidOperationException(
                "The transcode engine cannot accept the job (the queue is full or the engine is shutting down).");
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

        // Make the actually-selected encoder visible: a *_vaapi / *_videotoolbox encoder that then completes
        // means hardware encoding really happened (ffmpeg errors out if it can't init the device — it never
        // silently falls back to software mid-run).
        _logger.LogInformation(
            "Job {JobId}: encoding with {Encoder} ({Acceleration}).",
            job.JobId, EncoderName(job.Request.VideoCodec, hardware), HardwareLabel(hardware));

        // Encode to a temp file beside the destination and only rename it onto the real output on a clean
        // exit — so a failed, cancelled, or interrupted encode can never truncate/destroy a pre-existing
        // file at outputPath (ffmpeg's -y truncates its target the moment it starts).
        var outputPath = job.Request.OutputPath;
        var tempPath = TempOutputPath(outputPath, job.JobId);

        var psi = new ProcessStartInfo(_settings.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in BuildArguments(job, hardware, tempPath))
        {
            psi.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        var stderrTail = new StderrTail();
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // Watchdog: reset on every progress line; if ffmpeg goes silent for NoProgressTimeout the linked
            // token cancels the wait and we kill it. Linked to the worker token so shutdown still cancels too.
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            process.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
            process.OutputDataReceived += (_, e) =>
            {
                job.ApplyProgressLine(e.Data);
                if (e.Data is not null)
                {
                    try { watchdog.CancelAfter(NoProgressTimeout); }
                    catch (ObjectDisposedException) { /* The wait already returned; nothing to extend. */ }
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg failed to start.");
            }

            job.AttachProcess(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            watchdog.CancelAfter(NoProgressTimeout);

            // A cancel that arrived during start (before the process was attached) would have been a no-op;
            // now that the process is attached, honour it.
            if (job.CancelRequested)
            {
                TryKill(job);
            }

            try
            {
                await process.WaitForExitAsync(watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(job);
                if (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown: the worker token fired. Make sure ffmpeg can't outlive the engine, then stop
                    // gracefully — a normal shutdown is not a job failure.
                    job.Complete(JobState.Cancelled);
                }
                else
                {
                    // Watchdog: ffmpeg produced no progress for NoProgressTimeout (hung device init, stalled
                    // read, a special file that never returns) — fail the job rather than block the worker.
                    job.Fail();
                    JobFailed?.Invoke(this, job.JobId);
                    _logger.LogWarning(
                        "Job {JobId} killed: ffmpeg made no progress for {Timeout}. {Tail}",
                        job.JobId, NoProgressTimeout, stderrTail.Text);
                }

                return;
            }

            // WaitForExitAsync doesn't guarantee the async stdout/stderr callbacks have drained; the blocking
            // overload does, so the failure tail below carries ffmpeg's real last lines.
            process.WaitForExit();

            if (job.CancelRequested)
            {
                job.Complete(JobState.Cancelled);
                _logger.LogInformation("Job {JobId} cancelled.", job.JobId);
            }
            else if (process.ExitCode == 0 && TryPublishOutput(job, tempPath, outputPath, stderrTail))
            {
                job.Complete(JobState.Completed);
                JobCompleted?.Invoke(this, job.JobId);
                _logger.LogInformation("Job {JobId} completed.", job.JobId);
            }
            else if (process.ExitCode != 0)
            {
                job.Fail();
                JobFailed?.Invoke(this, job.JobId);
                _logger.LogWarning("Job {JobId} failed (ffmpeg exit {Code}). {Tail}", job.JobId, process.ExitCode, stderrTail.Text);
            }
            // else: ffmpeg exited 0 but the rename failed — TryPublishOutput already marked the job Failed.
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

            // Only ever discard the temp encode — the pre-existing file at outputPath is never touched on
            // failure/cancel. A completed job has already renamed its temp onto outputPath.
            if (job.State is JobState.Cancelled or JobState.Failed)
            {
                TryDeleteOutput(tempPath);
            }

            PruneTerminalJobs();
        }
    }

    /// <summary>Atomically renames the finished temp encode onto its real output path (replacing any
    /// existing file — the successful re-encode is the new good result). Returns false, and marks the job
    /// failed, if the rename can't be done, so a broken publish is never reported as a success.</summary>
    private bool TryPublishOutput(TranscodeJob job, string tempPath, string outputPath, StderrTail stderrTail)
    {
        try
        {
            File.Move(tempPath, outputPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            job.Fail();
            JobFailed?.Invoke(this, job.JobId);
            _logger.LogError(
                exception, "Job {JobId}: ffmpeg succeeded but publishing the output {Output} failed. {Tail}",
                job.JobId, outputPath, stderrTail.Text);
            return false;
        }
    }

    /// <summary>The in-progress encode target: a hidden temp file beside the real output that preserves its
    /// extension — ffmpeg picks the muxer from it — and is renamed onto the output only on success.</summary>
    private static string TempOutputPath(string outputPath, string jobId)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);
        return Path.Combine(directory, $".{stem}.{jobId}.part{extension}");
    }

    /// <summary>Evicts terminal jobs once they age past the retention window, and caps how many are kept, so
    /// the in-memory job dictionary and the SSE snapshot list stay bounded no matter how many jobs have run.
    /// Called after each job finishes.</summary>
    private void PruneTerminalJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in _jobs.Values)
        {
            if (job.IsTerminal && job.CompletedAt is { } completedAt && now - completedAt > TerminalJobRetention)
            {
                _jobs.TryRemove(job.JobId, out _);
            }
        }

        var terminal = _jobs.Values.Where(job => job.IsTerminal).ToList();
        if (terminal.Count > MaxRetainedTerminalJobs)
        {
            foreach (var job in terminal
                .OrderBy(job => job.CompletedAt ?? DateTimeOffset.MinValue)
                .Take(terminal.Count - MaxRetainedTerminalJobs))
            {
                _jobs.TryRemove(job.JobId, out _);
            }
        }
    }

    /// <summary>Builds the ffmpeg argument list. The video is either copied (remux) or re-encoded: VAAPI uses
    /// the proven software-decode → <c>hwupload</c> → hardware-encode chain (most compatible across arbitrary
    /// inputs); VideoToolbox (native macOS) maps straight to the platform encoder; AMF (native Windows + AMD)
    /// hardware-decodes on the VCN via D3D11VA and hardware-encodes with the <c>*_amf</c> encoders; software
    /// uses libx264/libx265 with an optional CRF. An optional downscale is applied with the GPU scaler on
    /// VAAPI and the CPU <c>scale</c> filter elsewhere. Audio and (for Matroska outputs) subtitle/attachment
    /// streams are copied — all of them, or the explicitly selected subset — so nothing is silently dropped.</summary>
    internal List<string> BuildArguments(TranscodeJob job, TranscodeHardware hardware, string? destinationPath = null)
    {
        var request = job.Request;
        // The muxer is inferred from the destination's extension; the temp path preserves it, so the
        // keepSubtitles (.mkv) decision below is unaffected whether we write the temp or the final path.
        var output = destinationPath ?? request.OutputPath;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };

        // Hardware decode setup only matters when we actually re-encode; a video copy never touches the GPU.
        if (!request.CopyVideo)
        {
            if (hardware == TranscodeHardware.Vaapi)
            {
                args.Add("-vaapi_device");
                args.Add(_settings.VaapiDevice);
            }
            else if (hardware == TranscodeHardware.Amf)
            {
                // Hardware-decode on the AMD VCN (D3D11VA), then hand the AMF encoder system-memory frames. The
                // explicit nv12 output format forces ffmpeg to download the decoded surfaces off the GPU —
                // without it the decoder keeps d3d11 surfaces that the *_amf encoders reject (format mismatch).
                // The system-memory hand-off (no fragile full-GPU surface chain) also keeps arbitrary inputs working.
                args.Add("-hwaccel");
                args.Add("d3d11va");
                args.Add("-hwaccel_output_format");
                args.Add("nv12");
            }
        }

        args.Add("-i");
        args.Add(request.InputPath);

        // Map the primary video stream (0:v:0, never a bare 0:v — that would also grab attached cover-art
        // "video" streams the hardware encoders reject), then the selected audio and subtitle streams.
        args.Add("-map");
        args.Add("0:v:0");
        AddStreamMaps(args, "a", request.AudioStreamIndexes);

        // Subtitles (and the attachment fonts that ASS subs render with) only when the output is Matroska:
        // mkv carries any subtitle/attachment codec, so a stream copy always works. Other containers (e.g.
        // mp4) reject most subtitle codecs on copy, which would fail the whole job, so we omit them there.
        var keepSubtitles = request.OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
        if (keepSubtitles && AddStreamMaps(args, "s", request.SubtitleStreamIndexes))
        {
            args.Add("-map");
            args.Add("0:t?");
        }

        // Video: copy (remux, lossless, HDR-safe) or re-encode with the selected encoder + optional downscale.
        if (request.CopyVideo)
        {
            args.Add("-c:v");
            args.Add("copy");
        }
        else
        {
            AddVideoEncode(args, request, hardware);
        }

        args.Add("-c:a");
        args.Add("copy");
        if (keepSubtitles)
        {
            args.Add("-c:s");
            args.Add("copy");
        }

        AddDefaultDisposition(args, "a", request.AudioStreamIndexes, request.DefaultAudioStreamIndex);
        if (keepSubtitles)
        {
            AddDefaultDisposition(args, "s", request.SubtitleStreamIndexes, request.DefaultSubtitleStreamIndex);
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(output);
        return args;
    }

    /// <summary>Adds <c>-map</c> entries for one stream type. A <c>null</c> selection copies every stream of
    /// that type (<c>0:a?</c> / <c>0:s?</c>, optional so a missing type doesn't fail the job); an explicit
    /// list maps those absolute input indexes in order. Returns whether any stream of the type is kept.</summary>
    private static bool AddStreamMaps(List<string> args, string kind, IReadOnlyList<int>? indexes)
    {
        if (indexes is null)
        {
            args.Add("-map");
            args.Add($"0:{kind}?");
            return true;
        }

        foreach (var index in indexes)
        {
            args.Add("-map");
            args.Add($"0:{index}");
        }

        return indexes.Count > 0;
    }

    /// <summary>Adds the video encoder (and optional downscale) for the resolved hardware. VAAPI scales on the
    /// GPU (<c>scale_vaapi</c> inside the hwupload chain); the other paths hand the encoder system-memory
    /// frames, so a plain CPU <c>scale</c> fits. The caller omits <see cref="TranscodeJobRequest.MaxHeight"/>
    /// when the source is already at or below the target, so this never upscales.</summary>
    private void AddVideoEncode(List<string> args, TranscodeJobRequest request, TranscodeHardware hardware)
    {
        var height = request.MaxHeight;
        switch (hardware)
        {
            case TranscodeHardware.Vaapi:
                args.Add("-vf");
                args.Add(height is { } h
                    ? $"format=nv12,hwupload,scale_vaapi=w=-2:h={h.ToString(CultureInfo.InvariantCulture)}"
                    : "format=nv12,hwupload");
                args.Add("-c:v");
                args.Add(VaapiEncoder(request.VideoCodec));
                break;
            case TranscodeHardware.VideoToolbox:
                AddCpuScale(args, height);
                args.Add("-c:v");
                args.Add(VideoToolboxEncoder(request.VideoCodec));
                break;
            case TranscodeHardware.Amf:
                AddCpuScale(args, height);
                args.Add("-c:v");
                args.Add(AmfEncoder(request.VideoCodec));
                break;
            default:
                AddCpuScale(args, height);
                args.Add("-c:v");
                args.Add(SoftwareEncoder(request.VideoCodec));
                if (request.Crf is { } crf)
                {
                    args.Add("-crf");
                    args.Add(crf.ToString(CultureInfo.InvariantCulture));
                }

                break;
        }
    }

    /// <summary>Adds a CPU <c>scale=-2:H</c> downscale (aspect kept, width snapped to an even number) when a
    /// target height is set.</summary>
    private static void AddCpuScale(List<string> args, int? maxHeight)
    {
        if (maxHeight is { } height)
        {
            args.Add("-vf");
            args.Add($"scale=-2:{height.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Forces exactly one mapped track of a type to be the container default. Needs the explicit
    /// index list to translate the chosen absolute index into the output-relative position ffmpeg's
    /// <c>-disposition:&lt;kind&gt;:&lt;pos&gt;</c> expects; with no list (copy-all) or no chosen default the
    /// source dispositions are left untouched.</summary>
    private static void AddDefaultDisposition(List<string> args, string kind, IReadOnlyList<int>? indexes, int? defaultIndex)
    {
        // Only act on a default that is actually one of the mapped tracks. Without this guard a stray index
        // (the endpoint rejects it, but this stays correct in isolation) would clear every default of the type.
        if (defaultIndex is null || indexes is null || !indexes.Contains(defaultIndex.Value))
        {
            return;
        }

        for (var position = 0; position < indexes.Count; position++)
        {
            args.Add($"-disposition:{kind}:{position}");
            args.Add(indexes[position] == defaultIndex.Value ? "default" : "0");
        }
    }

    /// <summary>Resolves <see cref="TranscodeHardware.Auto"/> to VideoToolbox on a native macOS host, AMF on
    /// a native Windows host with the AMD AMF runtime, VAAPI when a Linux render device is present, otherwise
    /// software. An explicit hardware choice the host cannot satisfy falls back to software with a warning
    /// rather than failing the job.</summary>
    internal TranscodeHardware ResolveHardware(TranscodeHardware requested)
    {
        var effective = requested == TranscodeHardware.Auto ? _settings.DefaultHardware : requested;
        // Probe the host once: Detect touches the filesystem (/dev/dri enumeration + AMF runtime check), and
        // both auto-detection and the post-resolution fallback checks below need a consistent view of it.
        var probe = HardwareProbe.Detect(_settings);
        if (effective is TranscodeHardware.Auto)
        {
            // The .NET process only reports its real OS when running natively (the docker runtime is Linux),
            // which is exactly where the platform encoders are reachable: VideoToolbox on macOS, AMF on a
            // Windows host whose AMD driver ships the runtime.
            if (OperatingSystem.IsMacOS())
            {
                effective = TranscodeHardware.VideoToolbox;
            }
            else if (OperatingSystem.IsWindows() && probe.AmfAvailable)
            {
                effective = TranscodeHardware.Amf;
            }
            else
            {
                effective = probe.VaapiAvailable ? TranscodeHardware.Vaapi : TranscodeHardware.None;
            }
        }

        if (effective == TranscodeHardware.Vaapi && !probe.VaapiAvailable)
        {
            _logger.LogWarning("VAAPI requested but no render device is available; falling back to software.");
            return TranscodeHardware.None;
        }

        if (effective == TranscodeHardware.VideoToolbox && !OperatingSystem.IsMacOS())
        {
            _logger.LogWarning("VideoToolbox requested but the engine is not running natively on macOS; falling back to software.");
            return TranscodeHardware.None;
        }

        if (effective == TranscodeHardware.Amf && !probe.AmfAvailable)
        {
            _logger.LogWarning("AMF requested but the AMD AMF runtime is not available (native Windows + AMD driver required); falling back to software.");
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

    private static string AmfEncoder(TranscodeVideoCodec codec) => codec switch
    {
        TranscodeVideoCodec.H264 => "h264_amf",
        TranscodeVideoCodec.Hevc => "hevc_amf",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static string SoftwareEncoder(TranscodeVideoCodec codec) => codec switch
    {
        TranscodeVideoCodec.H264 => "libx264",
        TranscodeVideoCodec.Hevc => "libx265",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static string EncoderName(TranscodeVideoCodec codec, TranscodeHardware hardware) => hardware switch
    {
        TranscodeHardware.Vaapi => VaapiEncoder(codec),
        TranscodeHardware.VideoToolbox => VideoToolboxEncoder(codec),
        TranscodeHardware.Amf => AmfEncoder(codec),
        _ => SoftwareEncoder(codec),
    };

    private static string HardwareLabel(TranscodeHardware hardware) => hardware switch
    {
        TranscodeHardware.Vaapi => "vaapi",
        TranscodeHardware.VideoToolbox => "videotoolbox",
        TranscodeHardware.Amf => "amf",
        _ => "software",
    };

    private async Task<double?> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        // Bound the probe so a FIFO/special file or a blocked read can't hang CreateAsync forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);
        var probeToken = timeoutCts.Token;
        try
        {
            var psi = new ProcessStartInfo(_settings.FfprobePath)
            {
                RedirectStandardOutput = true,
                // stderr is silenced by -v quiet and never read; leaving it un-redirected avoids a full
                // stderr pipe deadlocking ffprobe.
                RedirectStandardError = false,
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
                var stdout = await process.StandardOutput.ReadToEndAsync(probeToken);
                await process.WaitForExitAsync(probeToken);
                return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
            }
            catch (OperationCanceledException)
            {
                // Don't leave ffprobe running when the create request is cancelled or the probe times out.
                TryKillProcess(process);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired (not a client cancel) — fall back to byte-only progress rather than
            // hanging or failing the create.
            _logger.LogWarning("Probing the duration of {Input} timed out after {Timeout}; progress will be byte-only.", inputPath, ProbeTimeout);
            return null;
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
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ObjectDisposedException or Win32Exception)
        {
            // Process already gone or disposed, or the kill raced the OS teardown (notably Windows/AMF,
            // which surfaces as a Win32Exception) — either way there is nothing left to kill.
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
        // The same instance is registered three ways (FfmpegTranscodeEngine, ITranscodeEngine, and the hosted
        // service), so the DI container can dispose it more than once. Take the CTS atomically so Dispose is
        // idempotent — a second pass must not Cancel/Dispose an already-disposed source.
        if (Interlocked.Exchange(ref _cts, null) is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }

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
