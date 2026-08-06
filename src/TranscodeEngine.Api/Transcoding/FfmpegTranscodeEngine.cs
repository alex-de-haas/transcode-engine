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

    // How often the background sweep evicts aged-out terminal jobs. A periodic sweep (not just a post-job
    // one) is what ages out jobs that reach a terminal state without a worker finishing — notably a job
    // cancelled while still queued — so they don't linger when the engine then goes idle.
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(60);

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
        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_cts.Token)))
            .ToList();
        // A background sweep so terminal jobs are evicted on a timer, not only when the next job finishes.
        tasks.Add(Task.Run(() => MaintenanceLoopAsync(_cts.Token)));
        _workers = tasks.ToArray();

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
        var source = await ProbeSourceAsync(request.InputPath, cancellationToken);
        var duration = source.DurationSeconds;
        var inputSize = TryFileLength(request.InputPath);

        var jobId = Guid.NewGuid().ToString("n");
        var job = new TranscodeJob(jobId, request, duration, source.VideoPixelFormat);
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

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(MaintenanceInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                PruneTerminalJobs();
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
                // The wait can be cancelled three ways: a user cancel, engine shutdown, or the no-progress
                // watchdog. Kill ffmpeg, then classify — a cancel (either kind) must report Cancelled even if
                // the watchdog token happened to be the one that fired (a killed-but-slow-to-exit process
                // still emits no progress), and only a genuine watchdog trip is a Failed.
                TryKill(job);
                if (ClassifyInterruptedWait(job.CancelRequested, cancellationToken.IsCancellationRequested) == JobState.Cancelled)
                {
                    job.Complete(JobState.Cancelled);
                    _logger.LogInformation("Job {JobId} cancelled.", job.JobId);
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
        var terminal = _jobs.Values
            .Where(job => job.IsTerminal)
            .Select(job => (job.JobId, job.CompletedAt))
            .ToList();
        foreach (var jobId in SelectTerminalJobsToEvict(terminal, DateTimeOffset.UtcNow, TerminalJobRetention, MaxRetainedTerminalJobs))
        {
            _jobs.TryRemove(jobId, out _);
        }
    }

    /// <summary>Chooses which terminal jobs to evict: any older than <paramref name="retention"/>, plus — if
    /// more than <paramref name="maxRetained"/> survive that cut — the oldest of the rest, so retention is
    /// bounded by both age and count. Pure so the policy can be unit-tested directly.</summary>
    internal static IReadOnlyList<string> SelectTerminalJobsToEvict(
        IReadOnlyCollection<(string JobId, DateTimeOffset? CompletedAt)> terminalJobs,
        DateTimeOffset now,
        TimeSpan retention,
        int maxRetained)
    {
        var evict = new List<string>();
        var survivors = new List<(string JobId, DateTimeOffset? CompletedAt)>();
        foreach (var job in terminalJobs)
        {
            if (job.CompletedAt is { } completedAt && now - completedAt > retention)
            {
                evict.Add(job.JobId);
            }
            else
            {
                survivors.Add(job);
            }
        }

        if (survivors.Count > maxRetained)
        {
            foreach (var job in survivors
                .OrderBy(job => job.CompletedAt ?? DateTimeOffset.MinValue)
                .Take(survivors.Count - maxRetained))
            {
                evict.Add(job.JobId);
            }
        }

        return evict;
    }

    /// <summary>Classifies why the exit wait was cancelled: a user cancel or engine shutdown is a
    /// <see cref="JobState.Cancelled"/> — even if the no-progress watchdog token is the one that fired, since
    /// a killed-but-slow-to-exit process still emits no progress; only a wait cancelled with neither pending
    /// is a genuine watchdog trip, reported as <see cref="JobState.Failed"/>.</summary>
    internal static JobState ClassifyInterruptedWait(bool cancelRequested, bool shutdownRequested) =>
        cancelRequested || shutdownRequested ? JobState.Cancelled : JobState.Failed;

    /// <summary>Builds the ffmpeg argument list. The video is either copied (remux) or re-encoded: VAAPI uses
    /// the proven software-decode → <c>hwupload</c> → hardware-encode chain (most compatible across arbitrary
    /// inputs); VideoToolbox (native macOS) maps straight to the platform encoder; AMF (native Windows + AMD)
    /// hardware-decodes on the VCN via D3D11VA (the decoder downloads the surfaces to system memory) and
    /// hardware-encodes with the <c>*_amf</c> encoders; software uses libx264/libx265 with an optional CRF.
    /// An optional downscale is applied with the GPU scaler on VAAPI and the CPU <c>scale</c> filter
    /// elsewhere. Audio and (for Matroska outputs) subtitle/attachment
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
                // Hardware-decode on the AMD VCN (D3D11VA); the *_amf encoders want system-memory frames, so
                // the decoded surfaces have to come back off the GPU.
                //
                // -hwaccel_output_format is deliberately left unset. That is the only setting where ffmpeg
                // downloads each surface into *its own* software format: the transfer is
                // av_hwframe_transfer_data, which copies but cannot convert, and with no format requested it
                // takes the first format the surface offers (its sw_format — nv12 for 8-bit, p010 for 10-bit).
                // Both alternatives break on one depth or the other:
                //   * nv12  — pins the transfer to nv12, so every 10-bit (P010) source dies with EINVAL
                //             ("Failed to transfer data to output frame: -22").
                //   * d3d11 — keeps the surfaces on the GPU and needs an hwdownload filter, but the filter
                //             graph negotiates its format up front, before it can know what the frames carry;
                //             even `hwdownload,format=nv12|p010` settles on the first candidate and 10-bit
                //             sources die with "Invalid output format nv12 for hwframe download".
                // ffmpeg then converts to whatever the encoder advertises: hevc_amf takes p010 straight
                // through (10-bit preserved), h264_amf is 8-bit only and gets an inserted nv12 conversion.
                args.Add("-hwaccel");
                args.Add("d3d11va");
            }
        }

        args.Add("-i");
        args.Add(request.InputPath);

        // A merge names further files whose streams join the output; their ffmpeg input ordinals follow the
        // primary's, so the first additional input is 1.
        var additional = request.AdditionalInputs ?? [];
        foreach (var input in additional)
        {
            args.Add("-i");
            args.Add(input.Path);
        }

        // Map the primary video stream (0:v:0, never a bare 0:v — that would also grab attached cover-art
        // "video" streams the hardware encoders reject), then the selected audio and subtitle streams.
        args.Add("-map");
        args.Add("0:v:0");

        // Subtitles (and the attachment fonts that ASS subs render with) only when the output is Matroska:
        // mkv carries any subtitle/attachment codec, so a stream copy always works. Other containers (e.g.
        // mp4) reject most subtitle codecs on copy, which would fail the whole job, so we omit them there.
        var keepSubtitles = request.OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);

        // The ordered list of mapped streams per type. Output positions are assigned in map order, so this
        // is what turns an (input, absolute index) pair into the position -disposition and -metadata want.
        var audio = MappedStreams(request.AudioStreamIndexes, additional, input => input.AudioStreamIndexes);
        var subtitles = keepSubtitles
            ? MappedStreams(request.SubtitleStreamIndexes, additional, input => input.SubtitleStreamIndexes)
            : [];

        AddStreamMaps(args, "a", audio);
        if (keepSubtitles && AddStreamMaps(args, "s", subtitles))
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
            AddVideoEncode(args, request, hardware, job.SourcePixelFormat);
        }

        args.Add("-c:a");
        args.Add("copy");
        if (keepSubtitles)
        {
            args.Add("-c:s");
            args.Add("copy");
        }

        AddDefaultDisposition(args, "a", audio, request.DefaultAudioStreamIndex);
        if (keepSubtitles)
        {
            AddDefaultDisposition(args, "s", subtitles, request.DefaultSubtitleStreamIndex);
        }

        AddMetadataOverrides(args, "a", audio, request.MetadataOverrides);
        if (keepSubtitles)
        {
            AddMetadataOverrides(args, "s", subtitles, request.MetadataOverrides);
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(output);
        return args;
    }

    /// <summary>One mapped stream: which input file it comes from and its absolute index inside that file.
    /// A null <see cref="Index"/> is the "copy every stream of this type" selection, which has no single
    /// index and therefore no addressable output position.</summary>
    private readonly record struct MappedStream(int Input, int? Index);

    /// <summary>
    /// The ordered mapped streams of one type: the primary input's selection first, then each additional
    /// input's, which is the order ffmpeg assigns output positions in. A null primary selection stays a
    /// single "all of this type" entry — the endpoint rejects the combinations where that would leave a
    /// disposition or a metadata override unable to name a position.
    /// </summary>
    private static List<MappedStream> MappedStreams(
        IReadOnlyList<int>? primary,
        IReadOnlyList<AdditionalInput> additional,
        Func<AdditionalInput, IReadOnlyList<int>?> select)
    {
        var mapped = primary is null
            ? [new MappedStream(0, null)]
            : primary.Select(index => new MappedStream(0, index)).ToList();

        for (var ordinal = 0; ordinal < additional.Count; ordinal++)
        {
            foreach (var index in select(additional[ordinal]) ?? [])
            {
                mapped.Add(new MappedStream(ordinal + 1, index));
            }
        }

        return mapped;
    }

    /// <summary>Adds <c>-map</c> entries for one stream type. The "all of this type" entry becomes
    /// <c>0:a?</c> / <c>0:s?</c> (optional, so a missing type doesn't fail the job); every other entry maps
    /// one absolute index of its input. Returns whether any stream of the type is kept.</summary>
    private static bool AddStreamMaps(List<string> args, string kind, IReadOnlyList<MappedStream> mapped)
    {
        foreach (var stream in mapped)
        {
            args.Add("-map");
            args.Add(stream.Index is { } index ? $"{stream.Input}:{index}" : $"{stream.Input}:{kind}?");
        }

        return mapped.Count > 0;
    }

    /// <summary>Adds the video encoder (and optional downscale) for the resolved hardware. VAAPI scales on the
    /// GPU (<c>scale_vaapi</c> inside the hwupload chain); the other paths hand the encoder system-memory
    /// frames, so a plain CPU <c>scale</c> fits. The caller omits <see cref="TranscodeJobRequest.MaxHeight"/>
    /// when the source is already at or below the target, so this never upscales.</summary>
    private void AddVideoEncode(
        List<string> args,
        TranscodeJobRequest request,
        TranscodeHardware hardware,
        string? sourcePixelFormat)
    {
        var height = request.MaxHeight;
        switch (hardware)
        {
            case TranscodeHardware.Vaapi:
                // The upload format is the surface's sw_format, and therefore the depth the encoder sees: a
                // hardcoded nv12 converts every 10-bit source to 8 bit in software *before* hwupload, which
                // is invisible in the output except as banding on the gradients HDR material is full of.
                var tenBit = UsesTenBitVaapiUpload(request.VideoCodec, sourcePixelFormat);
                var upload = tenBit ? "p010" : "nv12";
                args.Add("-vf");
                args.Add(height is { } h
                    ? $"format={upload},hwupload,scale_vaapi=w=-2:h={h.ToString(CultureInfo.InvariantCulture)}"
                    : $"format={upload},hwupload");
                args.Add("-c:v");
                args.Add(VaapiEncoder(request.VideoCodec));
                if (tenBit)
                {
                    // vaapi_encode derives the profile from the surface format, but naming it keeps a driver
                    // that would rather offer Main from quietly winning the negotiation.
                    args.Add("-profile:v");
                    args.Add("main10");
                }

                break;
            case TranscodeHardware.VideoToolbox:
                AddCpuScale(args, height);
                args.Add("-c:v");
                args.Add(VideoToolboxEncoder(request.VideoCodec));
                break;
            case TranscodeHardware.Amf:
                // The decoder already hands back system-memory frames (see the -hwaccel setup), so no
                // hwdownload filter — just the optional CPU downscale.
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

    /// <summary>
    /// Whether the VAAPI chain uploads <c>p010</c> (10-bit surfaces) rather than <c>nv12</c>. True only for a
    /// deeper-than-8-bit source encoded to HEVC: no shipping VAAPI driver exposes an H.264 High 10 *encode*
    /// entrypoint, so uploading p010 for H.264 would turn today's working (if 8-bit) job into a hard
    /// "no usable encoding profile" failure. An unreadable or unrecognised source format keeps nv12 — the
    /// behaviour every input had before, which is the safe direction to be wrong in.
    /// </summary>
    internal static bool UsesTenBitVaapiUpload(TranscodeVideoCodec codec, string? sourcePixelFormat) =>
        codec == TranscodeVideoCodec.Hevc && SourceBitDepth(sourcePixelFormat) > 8;

    /// <summary>
    /// Bits per component of an ffmpeg pixel format name. The depth is the digit run right after the last
    /// <c>p</c> (the planar marker), with the endianness suffix stripped first: <c>yuv420p10le</c> → 10,
    /// <c>p010le</c> → 10, <c>yuv420p</c> → 8. Anything without that shape — <c>nv12</c>, <c>rgb24</c>,
    /// <c>yuyv422</c>, whose digits are a subsampling/packing code rather than a depth — reads as 8, so a
    /// format this does not model can only ever keep the pre-existing nv12 path, never mis-select p010.
    /// </summary>
    internal static int SourceBitDepth(string? pixelFormat)
    {
        if (pixelFormat is not { Length: > 0 } name)
        {
            return 8;
        }

        var trimmed = name.EndsWith("le", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith("be", StringComparison.OrdinalIgnoreCase)
            ? name[..^2]
            : name;

        var planar = trimmed.LastIndexOf('p');
        if (planar < 0)
        {
            return 8;
        }

        var end = planar + 1;
        while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
        {
            end++;
        }

        return end > planar + 1 &&
               int.TryParse(trimmed[(planar + 1)..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
            ? depth
            : 8;
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
    private static void AddDefaultDisposition(List<string> args, string kind, IReadOnlyList<MappedStream> mapped, int? defaultIndex)
    {
        // The chosen default is an absolute index in the primary input. Only act when it is actually one of
        // the mapped tracks: without this guard a stray index (the endpoint rejects it, but this stays
        // correct in isolation) would clear every default of the type.
        if (defaultIndex is null || !mapped.Any(stream => stream is { Input: 0, Index: { } index } && index == defaultIndex.Value))
        {
            return;
        }

        for (var position = 0; position < mapped.Count; position++)
        {
            args.Add($"-disposition:{kind}:{position}");
            args.Add(mapped[position] is { Input: 0, Index: { } index } && index == defaultIndex.Value ? "default" : "0");
        }
    }

    /// <summary>
    /// Writes the requested language/title onto the output positions their streams landed in. A field the
    /// request left null is not emitted at all, so the source stream's own tag survives — an operator who
    /// renames one track must not silently freeze the rest.
    /// </summary>
    private static void AddMetadataOverrides(
        List<string> args,
        string kind,
        IReadOnlyList<MappedStream> mapped,
        IReadOnlyList<StreamMetadataOverride>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return;
        }

        for (var position = 0; position < mapped.Count; position++)
        {
            if (mapped[position].Index is not { } index)
            {
                continue;
            }

            var input = mapped[position].Input;
            // At most one override per stream: the endpoint rejects duplicates, so there is nothing to
            // choose between here and no instruction of the caller's is silently dropped.
            var match = overrides.FirstOrDefault(o => o.Input == input && o.StreamIndex == index);
            if (match is null)
            {
                continue;
            }

            if (match.Language is { Length: > 0 } language)
            {
                args.Add($"-metadata:s:{kind}:{position}");
                args.Add($"language={language}");
            }

            if (match.Title is { Length: > 0 } title)
            {
                args.Add($"-metadata:s:{kind}:{position}");
                args.Add($"title={title}");
            }
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

    /// <summary>What the create-time ffprobe learns about the input: the duration progress percentages are
    /// computed from, and the primary video stream's pixel format, which decides the bit depth the VAAPI
    /// chain uploads at. Either field is null when ffprobe could not report it (an audio-only merge source
    /// has no <c>pix_fmt</c>); both callers degrade rather than fail.</summary>
    internal readonly record struct SourceProbe(double? DurationSeconds, string? VideoPixelFormat);

    private async Task<SourceProbe> ProbeSourceAsync(string inputPath, CancellationToken cancellationToken)
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
                // Only the primary video stream's pix_fmt matters (BuildArguments maps 0:v:0); -select_streams
                // does not touch the format section, so the duration still comes back.
                "-select_streams", "v:0",
                "-show_entries", "format=duration:stream=pix_fmt",
                // Keys are kept (no nokey=1): the two entries live in different sections, so the output is
                // parsed by name rather than by line order.
                "-of", "default=noprint_wrappers=1",
                inputPath,
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return default;
            }

            try
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(probeToken);
                await process.WaitForExitAsync(probeToken);
                return ParseSourceProbe(stdout);
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
            _logger.LogWarning("Probing {Input} timed out after {Timeout}; progress will be byte-only.", inputPath, ProbeTimeout);
            return default;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not probe {Input}; progress will be byte-only.", inputPath);
            return default;
        }
    }

    /// <summary>Reads ffprobe's <c>key=value</c> lines. A key ffprobe did not emit stays null — an
    /// audio-only input reports no <c>pix_fmt</c>, and a stream-less file reports no <c>duration</c>.</summary>
    internal static SourceProbe ParseSourceProbe(string stdout)
    {
        double? duration = null;
        string? pixelFormat = null;
        foreach (var line in stdout.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "duration" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds):
                    duration = seconds;
                    break;
                case "pix_fmt" when value.Length > 0 && value != "unknown":
                    pixelFormat = value;
                    break;
            }
        }

        return new SourceProbe(duration, pixelFormat);
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
