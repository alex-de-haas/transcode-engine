using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// The Dolby Vision profile 7 → 8.1 conversion: a video-copy job that runs as four tool stages instead of
/// one ffmpeg invocation.
/// <para>
/// ffmpeg cannot do this. A UHD Blu-ray's profile 7 keeps its RPU metadata in the enhancement layer, which
/// Matroska stores in per-block <c>BlockAdditions</c>; rewriting it to the single-layer profile 8.1 that
/// Apple TV and Infuse play as Dolby Vision is <c>dovi_tool</c>'s job, and writing the Matroska mapping
/// that announces the result is <c>mkvmerge</c>'s. So the picture takes a path of its own —
/// <c>mkvextract</c> writes base and enhancement layer as one elementary stream, <c>dovi_tool</c> rewrites
/// the RPU and drops the enhancement layer, <c>mkvmerge</c> assembles the output — while ffmpeg composes
/// everything <em>but</em> the picture exactly as it does for any other job: audio targets, subtitles, merged
/// inputs, metadata overrides, default flags. The two halves meet in mkvmerge.
/// </para>
/// <para>
/// Each stage is one process, run under the same cancel and no-progress watchdog every job has. Only the
/// first speaks ffmpeg's <c>-progress</c> dialect; the others report progress by the growth of the file
/// they write, which is also what proves they are alive — <c>dovi_tool</c> draws its progress bar only on a
/// terminal, and would look hung on a pipe for the whole of a two-hour film.
/// </para>
/// </summary>
public sealed partial class FfmpegTranscodeEngine
{
    /// <summary>ffmpeg (tracks), mkvextract, dovi_tool, mkvmerge — the snapshot's percentage is split evenly
    /// between them, each being one pass over roughly the whole file.</summary>
    internal const int DolbyVisionStages = 4;

    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromSeconds(1);

    // Bound on `mkvmerge --identify`: it reads headers only, and a blocked read must not hang the worker.
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The files a conversion writes on its way to the output, all hidden and all beside it: the
    /// ffmpeg composition of everything but the picture, the source's base+enhancement layer as an elementary
    /// stream, and the converted profile 8.1 stream.</summary>
    internal sealed record DolbyVisionIntermediates(string Tracks, string SourceLayers, string ConvertedLayer);

    internal static DolbyVisionIntermediates DolbyVisionIntermediatePaths(string outputPath, string jobId)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        return new DolbyVisionIntermediates(
            Path.Combine(directory, $".{stem}.{jobId}.tracks.mkv"),
            Path.Combine(directory, $".{stem}.{jobId}.bl-el.hevc"),
            Path.Combine(directory, $".{stem}.{jobId}.dv81.hevc"));
    }

    /// <summary>
    /// Whether the input can be converted at all, from its create-time probe. Only a dual-layer profile 7
    /// has anything to rewrite; a profile 8 is already what the conversion produces, a profile 5 has no
    /// HDR10 base layer to fall back on, a stream without a record has no Dolby Vision, and a file without
    /// a video stream has nothing to convert. An input the probe could not read at all passes: the check
    /// after the last stage catches a result that is not profile 8.1, and degrading like every other probe
    /// here beats refusing a job over a probe that timed out.
    /// </summary>
    internal static string? DolbyVisionConversionError(SourceProbe source)
    {
        if (!source.Probed)
        {
            return null;
        }

        if (!source.HasVideoStream)
        {
            return "the input has no video stream to convert.";
        }

        if (source.DolbyVision is not { } record)
        {
            return "the input's video carries no Dolby Vision configuration record, so there is nothing to convert to profile 8.1.";
        }

        return record.Profile == 7
            ? null
            : $"the input's video is Dolby Vision profile {record.Profile.ToString(CultureInfo.InvariantCulture)}, not the dual-layer profile 7 this conversion rewrites.";
    }

    /// <summary>What the output has to be for the job to count as done: a Dolby Vision record saying profile 8
    /// with base-layer compatibility id 1. Anything else — no record, another profile — is a failed job, not a
    /// published file with the wrong metadata.</summary>
    internal static string? DolbyVisionOutputError(SourceProbe output)
    {
        if (output.DolbyVision is not { } record)
        {
            return "the converted output carries no Dolby Vision configuration record.";
        }

        return record is { Profile: 8, CompatibilityId: 1 }
            ? null
            : $"the converted output is Dolby Vision profile {record.Profile.ToString(CultureInfo.InvariantCulture)} with base-layer compatibility id {record.CompatibilityId.ToString(CultureInfo.InvariantCulture)}, not profile 8.1.";
    }

    /// <summary>
    /// Refuses a conversion the output mount cannot hold. At its peak a conversion keeps two copies of the
    /// video beside the composed tracks — the converted elementary stream and the file mkvmerge is writing
    /// — so twice the input is the honest requirement; the source layers are deleted before mkvmerge starts,
    /// which is what keeps it at two rather than three. Null when either figure is unknown: a check that
    /// cannot be made must not fail a job.
    /// </summary>
    internal static string? FreeSpaceError(long? availableBytes, long? inputBytes)
    {
        if (availableBytes is not { } available || inputBytes is not { } input)
        {
            return null;
        }

        var required = input * 2;
        return available < required
            ? $"the output location has {Gigabytes(available)} GB free; converting this {Gigabytes(input)} GB file needs about {Gigabytes(required)} GB for its intermediates."
            : null;
    }

    private static string Gigabytes(long bytes) =>
        Math.Round(bytes / 1_073_741_824.0, 1).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Maps a stage's own 0–100 into its slice of the job's percentage: stage 2 of 4 at 50 % is
    /// 62.5 % of the job. Clamped, because a file growing past its expected size must not report past its
    /// stage.</summary>
    internal static double StagePercent(int stage, int stageCount, double within) =>
        (stage * 100 + Math.Clamp(within, 0, 100)) / stageCount;

    /// <summary><c>mkvextract input.mkv tracks ID:out.hevc</c> — the video track as an Annex B elementary
    /// stream, base and enhancement layer interleaved, which is how mkvextract writes a track that carries
    /// <c>BlockAdditions</c>.</summary>
    internal static List<string> BuildMkvextractArguments(string inputPath, int trackId, string destination) =>
        [inputPath, "tracks", $"{trackId.ToString(CultureInfo.InvariantCulture)}:{destination}"];

    /// <summary><c>dovi_tool -m 2 convert --discard in.hevc -o out.hevc</c>: mode 2 rewrites the RPU to
    /// profile 8.1, <c>--discard</c> drops the enhancement layer, and the base layer is copied byte for byte.</summary>
    internal static List<string> BuildDoviToolArguments(string sourceLayers, string destination) =>
        ["-m", "2", "convert", "--discard", sourceLayers, "-o", destination];

    /// <summary>
    /// Whether the ffmpeg stage would map any stream at all. ffmpeg with no <c>-map</c> falls back to automatic
    /// stream selection — video, audio and subtitles the caller may have excluded on purpose — so a
    /// composition that selects nothing is not run; the output is then the converted video alone. Explicit
    /// selections answer by their count; a null selection copies every stream of its kind and so maps
    /// something exactly when the input has one (a null subtitle selection also carries attachments). An
    /// additional input always selects at least one stream — the endpoint requires it. An unknown stream list
    /// (the probe failed) answers true: running a stage that turns out empty fails honestly, skipping one
    /// that was needed loses tracks.
    /// </summary>
    internal static bool TracksStageMapsAnything(TranscodeJobRequest request, IReadOnlyList<ProbedStream> streams)
    {
        if (streams.Count == 0 || request.AdditionalInputs is { Count: > 0 })
        {
            return true;
        }

        var audio = request.AudioStreamIndexes is { } audioSelection
            ? audioSelection.Count > 0
            : streams.Any(stream => stream.Kind == "audio");
        var subtitles = request.SubtitleStreamIndexes is { } subtitleSelection
            ? subtitleSelection.Count > 0
            : streams.Any(stream => stream.Kind is "subtitle" or "attachment");
        return audio || subtitles;
    }

    /// <summary>
    /// <c>mkvmerge</c> assembling the output: the converted elementary stream first, then every track of the
    /// composition except a video (it has none, but saying so costs nothing) — or the video alone when the
    /// composition was skipped because nothing was selected. An elementary stream carries no timestamps, so
    /// the default duration is the source's frame rate — without it mkvmerge assumes 25 fps and a 23.976 film
    /// drifts a minute over two hours — and no tags, so the source's language and title are put back.
    /// mkvmerge reads the RPU and writes the Matroska Dolby Vision mapping itself.
    /// </summary>
    internal static List<string> BuildMkvmergeArguments(string destination, string convertedLayer, string? tracks, SourceProbe source)
    {
        var args = new List<string> { "--output", destination };
        if (source.VideoFrameRate is { Length: > 0 } frameRate)
        {
            args.Add("--default-duration");
            args.Add($"0:{frameRate}fps");
        }

        if (source.VideoLanguage is { Length: > 0 } language)
        {
            args.Add("--language");
            args.Add($"0:{language}");
        }

        if (source.VideoTitle is { Length: > 0 } title)
        {
            args.Add("--track-name");
            args.Add($"0:{title}");
        }

        args.Add(convertedLayer);
        if (tracks is not null)
        {
            args.Add("--no-video");
            args.Add(tracks);
        }

        return args;
    }

    /// <summary>The tools' output is UTF-8 whatever the host's console code page is — mkvmerge writes it so on
    /// every platform — and .NET's default for a redirected pipe on Windows is the console's own code page,
    /// under which a UTF-8 document is mojibake and its byte-order mark three stray characters. Said once
    /// here so every process this runner starts reads the same way.</summary>
    private static readonly Encoding ToolOutput = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>mkvmerge --no-bom -J input</c>: the identification as JSON, without the byte-order mark
    /// mkvmerge otherwise puts in front of redirected output on Windows.</summary>
    internal static List<string> BuildIdentifyArguments(string inputPath) =>
        ["--no-bom", "--identification-format", "json", "--identify", inputPath];

    /// <summary>The id of the first video track in <c>mkvmerge --identify</c>'s JSON, which is what mkvextract
    /// addresses tracks by. ffprobe's stream index is not the same numbering — attachments are streams to
    /// ffprobe and not tracks to mkvmerge — so it is asked rather than assumed. Null when there is none or
    /// the document cannot be read. A byte-order mark or whitespace ahead of the document is skipped rather
    /// than counted as a document that cannot be read.</summary>
    internal static int? ParseVideoTrackId(string identifyJson)
    {
        try
        {
            using var document = JsonDocument.Parse(identifyJson.AsMemory().TrimStart('\uFEFF').Trim());
            if (!document.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                if (track.ValueKind == JsonValueKind.Object &&
                    track.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "video", StringComparison.OrdinalIgnoreCase) &&
                    track.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    return id.GetInt32();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Free bytes on the volume the output is written to; null when the question cannot be answered,
    /// which the caller reads as "cannot check" rather than "no space".</summary>
    internal static long? AvailableBytes(string outputPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>How a stage reports progress and proves it is alive: ffmpeg's own <c>-progress</c> lines, or
    /// the growth of the file it writes against the size it is expected to reach.</summary>
    private sealed record StageProgress(bool FromFfmpeg, string? GrowthPath, long? ExpectedBytes, bool IsOutput)
    {
        public static readonly StageProgress Ffmpeg = new(true, null, null, false);

        public static StageProgress Growth(string path, long? expectedBytes, bool isOutput = false) =>
            new(false, path, expectedBytes, isOutput);
    }

    private async Task RunDolbyVisionJobAsync(TranscodeJob job, TranscodeHardware hardware, CancellationToken cancellationToken)
    {
        var request = job.Request;
        var outputPath = request.OutputPath!;
        var finalTemp = TempOutputPath(outputPath, job.JobId);
        var temps = DolbyVisionIntermediatePaths(outputPath, job.JobId);
        var stderrTail = new StderrTail();
        job.ReportProgress(0);

        _logger.LogInformation(
            "Job {JobId}: converting Dolby Vision profile 7 to 8.1 — picture copied, enhancement layer dropped.",
            job.JobId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            if (FreeSpaceError(AvailableBytes(outputPath), TryFileLength(request.InputPath)) is { } space)
            {
                FailDolbyVisionJob(job, space, stderrTail);
                return;
            }

            var trackId = await IdentifyVideoTrackAsync(request.InputPath, cancellationToken);
            if (trackId is null)
            {
                FailDolbyVisionJob(job, "mkvmerge could not identify a video track in the input.", stderrTail);
                return;
            }

            // 1. Everything but the picture, composed by ffmpeg exactly as any other job's tracks would be —
            // unless the request selects nothing, in which case there is nothing to compose and the output is
            // the converted video alone. (ffmpeg given no -map would select streams on its own.)
            string? tracks = null;
            if (TracksStageMapsAnything(request, await ProbeStreamsAsync(request.InputPath, cancellationToken)))
            {
                tracks = temps.Tracks;
                var compose = new ProcessStartInfo(_settings.FfmpegPath);
                foreach (var arg in BuildArguments(job, hardware, [tracks]))
                {
                    compose.ArgumentList.Add(arg);
                }

                if (!await RunStageAsync(job, compose, "ffmpeg", 0, StageProgress.Ffmpeg, stderrTail, cancellationToken))
                {
                    return;
                }
            }
            else
            {
                _logger.LogInformation("Job {JobId}: no audio or subtitle track selected; the output carries the video alone.", job.JobId);
                job.ReportProgress(StagePercent(1, DolbyVisionStages, 0));
            }

            // 2. The source's base and enhancement layer, as one elementary stream.
            var extract = new ProcessStartInfo(_settings.MkvextractPath);
            foreach (var arg in BuildMkvextractArguments(request.InputPath, trackId.Value, temps.SourceLayers))
            {
                extract.ArgumentList.Add(arg);
            }

            // The video is most of a remux, so the whole input is a close upper bound for the stream's size.
            var extractProgress = StageProgress.Growth(temps.SourceLayers, TryFileLength(request.InputPath));
            if (!await RunStageAsync(job, extract, "mkvextract", 1, extractProgress, stderrTail, cancellationToken))
            {
                return;
            }

            // 3. The RPU rewritten to profile 8.1, the enhancement layer gone; the base layer copied.
            var convert = new ProcessStartInfo(_settings.DoviToolPath);
            foreach (var arg in BuildDoviToolArguments(temps.SourceLayers, temps.ConvertedLayer))
            {
                convert.ArgumentList.Add(arg);
            }

            var convertProgress = StageProgress.Growth(temps.ConvertedLayer, TryFileLength(temps.SourceLayers));
            if (!await RunStageAsync(job, convert, "dovi_tool", 2, convertProgress, stderrTail, cancellationToken))
            {
                return;
            }

            // The source layers are not needed again, and deleting them now is what keeps the peak at two
            // copies of the video rather than three (see FreeSpaceError).
            TryDeleteOutput(temps.SourceLayers);

            // 4. The output, assembled by mkvmerge. Exit code 1 is "finished with warnings" and still wrote
            // the file; only 2 is an error.
            var mux = new ProcessStartInfo(_settings.MkvmergePath);
            foreach (var arg in BuildMkvmergeArguments(finalTemp, temps.ConvertedLayer, tracks, job.Source))
            {
                mux.ArgumentList.Add(arg);
            }

            var expected = (TryFileLength(temps.ConvertedLayer) ?? 0) + (tracks is null ? 0 : TryFileLength(tracks) ?? 0);
            var muxProgress = StageProgress.Growth(finalTemp, expected > 0 ? expected : null, isOutput: true);
            if (!await RunStageAsync(job, mux, "mkvmerge", 3, muxProgress, stderrTail, cancellationToken, maxAcceptedExitCode: 1))
            {
                return;
            }

            // The whole point of the job is the metadata, so the output is checked for it before it is
            // published: a file that came out as HDR10, or as some other profile, is a failure and not a
            // version with a misleading name.
            var produced = await ProbeSourceAsync(finalTemp, cancellationToken);
            if (DolbyVisionOutputError(produced) is { } outputError)
            {
                FailDolbyVisionJob(job, outputError, stderrTail);
                return;
            }

            if (TryPublishOutputs(job, [finalTemp], [outputPath], stderrTail))
            {
                job.ReportOutputSize(TryFileLength(outputPath) ?? 0);
                job.Complete(JobState.Completed);
                JobCompleted?.Invoke(this, job.JobId);
                _logger.LogInformation("Job {JobId} completed: Dolby Vision profile 8.1 written to {Output}.", job.JobId, outputPath);
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
            job.DetachProcess();

            // The intermediates are never anything but scratch; the output temp is discarded unless the job
            // published it, and a pre-existing file at the output path is never touched on failure/cancel.
            foreach (var path in new[] { temps.Tracks, temps.SourceLayers, temps.ConvertedLayer })
            {
                TryDeleteOutput(path);
            }

            if (job.State is JobState.Cancelled or JobState.Failed)
            {
                TryDeleteOutput(finalTemp);
            }

            PruneTerminalJobs();
        }
    }

    /// <summary>
    /// Runs one stage's process under the job's cancel and no-progress watchdog, and reports its slice of the
    /// progress. Returns true when the stage finished acceptably and the job may go on; false when it did not,
    /// with the job already marked cancelled or failed and the reason logged.
    /// </summary>
    private async Task<bool> RunStageAsync(
        TranscodeJob job,
        ProcessStartInfo psi,
        string tool,
        int stage,
        StageProgress progress,
        StderrTail stderrTail,
        CancellationToken cancellationToken,
        int maxAcceptedExitCode = 0)
    {
        // A cancel that arrived while the previous stage was finishing must not start the next one.
        if (job.CancelRequested)
        {
            job.Complete(JobState.Cancelled);
            _logger.LogInformation("Job {JobId} cancelled.", job.JobId);
            return false;
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = ToolOutput;
        psi.StandardErrorEncoding = ToolOutput;
        psi.UseShellExecute = false;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var exited = new CancellationTokenSource();

        void Extend()
        {
            try { watchdog.CancelAfter(NoProgressTimeout); }
            catch (ObjectDisposedException) { /* The wait already returned; nothing to extend. */ }
        }

        process.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // ffmpeg's progress lines carry the percentage; the MKVToolNix tools print "Progress: N%" lines
            // that only prove liveness here — the file they write is what measures them.
            if (progress.FromFfmpeg)
            {
                job.ApplyProgressLine(e.Data);
                if (job.FfmpegPercent is { } percent)
                {
                    job.ReportProgress(StagePercent(stage, DolbyVisionStages, percent));
                }
            }

            Extend();
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"{tool} failed to start.");
        }

        job.AttachProcess(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        Extend();

        if (job.CancelRequested)
        {
            TryKill(job);
        }

        var growth = progress.GrowthPath is { } path
            ? PollGrowthAsync(job, path, progress.ExpectedBytes, progress.IsOutput, stage, Extend, exited.Token)
            : Task.CompletedTask;

        var interrupted = false;
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            TryKill(job);
        }
        finally
        {
            exited.Cancel();
            await growth;
        }

        // WaitForExitAsync doesn't guarantee the async stdout/stderr callbacks have drained; the blocking
        // overload does, so the failure tail below carries the tool's real last lines.
        process.WaitForExit();
        job.DetachProcess();

        if (interrupted)
        {
            if (ClassifyInterruptedWait(job.CancelRequested, cancellationToken.IsCancellationRequested) == JobState.Cancelled)
            {
                job.Complete(JobState.Cancelled);
                _logger.LogInformation("Job {JobId} cancelled during {Tool}.", job.JobId, tool);
            }
            else
            {
                job.Fail();
                JobFailed?.Invoke(this, job.JobId);
                _logger.LogWarning(
                    "Job {JobId} killed: {Tool} made no progress for {Timeout}. {Tail}",
                    job.JobId, tool, NoProgressTimeout, stderrTail.Text);
            }

            return false;
        }

        if (job.CancelRequested)
        {
            job.Complete(JobState.Cancelled);
            _logger.LogInformation("Job {JobId} cancelled.", job.JobId);
            return false;
        }

        if (process.ExitCode > maxAcceptedExitCode)
        {
            FailDolbyVisionJob(job, $"{tool} exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.", stderrTail);
            return false;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Job {JobId}: {Tool} finished with warnings (exit {Code}). {Tail}", job.JobId, tool, process.ExitCode, stderrTail.Text);
        }

        job.ReportProgress(StagePercent(stage + 1, DolbyVisionStages, 0));
        return true;
    }

    /// <summary>Measures a stage by the file it writes: each growth extends the watchdog, and the size against
    /// the expected total is the stage's percentage. Runs until the process exits.</summary>
    private static async Task PollGrowthAsync(
        TranscodeJob job,
        string path,
        long? expectedBytes,
        bool isOutput,
        int stage,
        Action extendWatchdog,
        CancellationToken exited)
    {
        var last = -1L;
        try
        {
            using var timer = new PeriodicTimer(GrowthPollInterval);
            while (await timer.WaitForNextTickAsync(exited))
            {
                var length = TryFileLength(path) ?? 0;
                if (length > last)
                {
                    last = length;
                    extendWatchdog();
                }

                if (expectedBytes is { } expected && expected > 0)
                {
                    job.ReportProgress(StagePercent(stage, DolbyVisionStages, length * 100.0 / expected));
                }

                if (isOutput)
                {
                    job.ReportOutputSize(length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The process exited; the poll is over.
        }
    }

    private void FailDolbyVisionJob(TranscodeJob job, string reason, StderrTail stderrTail)
    {
        job.Fail();
        JobFailed?.Invoke(this, job.JobId);
        _logger.LogWarning("Job {JobId} failed: {Reason} {Tail}", job.JobId, reason, stderrTail.Text);
    }

    /// <summary>The first few hundred characters of a tool's output, enough to see what went wrong without
    /// putting a whole identification into one log line.</summary>
    private static string Head(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Length <= 400 ? text.Trim() : text[..400].Trim() + "…";

    /// <summary>Asks <c>mkvmerge --identify</c> for the input's video track id. Bounded and best-effort like
    /// every probe here: null when it cannot answer, which fails the job with a reason rather than guessing
    /// a track.</summary>
    private async Task<int?> IdentifyVideoTrackAsync(string inputPath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(IdentifyTimeout);
        try
        {
            var psi = new ProcessStartInfo(_settings.MkvmergePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ToolOutput,
                StandardErrorEncoding = ToolOutput,
                UseShellExecute = false,
            };
            foreach (var arg in BuildIdentifyArguments(inputPath))
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
                // Both pipes drained so a chatty identify cannot block on a full one.
                var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                var errors = await stderr;
                var trackId = ParseVideoTrackId(stdout);
                if (trackId is null)
                {
                    // The one answer that used to be silent: what mkvmerge actually said, so the next failure
                    // of this kind is diagnosable from the log rather than reproduced by hand on the host.
                    _logger.LogWarning(
                        "mkvmerge --identify found no video track in {Input} (exit {Code}). stdout: {Stdout} stderr: {Stderr}",
                        inputPath, process.ExitCode, Head(stdout), Head(errors));
                }

                return trackId;
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Identifying the tracks of {Input} timed out after {Timeout}.", inputPath, IdentifyTimeout);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not identify the tracks of {Input}.", inputPath);
            return null;
        }
    }
}
