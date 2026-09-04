using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TranscodeEngine.Api.Probing;
using TranscodeEngine.Api.Realtime;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Api;

/// <summary>Maps the control API: create/list/inspect/cancel/remove jobs, plus the SSE event stream.
/// Engine records (<see cref="JobDescriptor"/>/<see cref="JobSnapshot"/>) are returned directly.</summary>
public static class TranscodeEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How two resolved paths are compared for "the same file". Ordinal is right on Linux, which the docker
    /// runtime is; a native macOS or Windows host is normally case-insensitive, where two spellings name one
    /// file — and an output that is really the input, or really another output, is published over it by
    /// <c>File.Move(overwrite: true)</c> once ffmpeg succeeds.
    /// <para>
    /// The heuristic is deliberately wrong in the safe direction: being case-insensitive on a volume that is
    /// not merely refuses a request the caller can rephrase, while being ordinal on a volume that is
    /// case-insensitive destroys a file.
    /// </para>
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static void MapTranscodeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/jobs", async (CreateJobRequest request, ITranscodeEngine engine, TranscodeEngineSettings settings, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.InputPath))
            {
                return Results.BadRequest(new { error = "inputPath is required." });
            }

            // Naming outputs makes the job an extraction, which shares this endpoint but not the shape below:
            // it composes nothing, so none of the composition arguments apply and it takes its own path out
            // of here rather than threading a flag through every check.
            if (request.Outputs is { Count: > 0 } extractionOutputs)
            {
                return await CreateExtractionAsync(request, extractionOutputs, engine, settings, ct);
            }

            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                return Results.BadRequest(new { error = "outputPath is required." });
            }

            // "copy" remuxes the video untouched; the codec/hardware/quality/height knobs are then irrelevant.
            //
            // Naming additional inputs no longer implies it. Appending tracks from other files says nothing
            // about what happens to the video, and shrinking a remux while folding its dubs in is one job,
            // not two passes over the same gigabytes — so a merge may carry a real codec.
            //
            // What a merge keeps is the *default*: with no videoCodec at all it still copies, where an
            // ordinary job would encode to HEVC. Re-encoding is the expensive, lossy direction and must never
            // be what a caller gets by omission — least of all one written against the old rule.
            var additionalInputs = request.AdditionalInputs ?? [];
            var isMerge = additionalInputs.Count > 0;
            var copyVideo = string.Equals(request.VideoCodec?.Trim(), "copy", StringComparison.OrdinalIgnoreCase) ||
                (isMerge && string.IsNullOrWhiteSpace(request.VideoCodec));

            var codec = TranscodeVideoCodec.Hevc;
            if (!copyVideo && !TryParseCodec(request.VideoCodec, out codec))
            {
                return Results.BadRequest(new { error = $"videoCodec '{request.VideoCodec}' is not supported (use 'h264', 'hevc' or 'copy')." });
            }

            if (!TryParseHardware(request.HardwareAcceleration, out var hardware))
            {
                return Results.BadRequest(new { error = $"hardwareAcceleration '{request.HardwareAcceleration}' is not supported (use 'auto', 'vaapi', 'videotoolbox', 'amf' or 'none')." });
            }

            TranscodeQualityLevel? qualityLevel = null;
            if (!string.IsNullOrWhiteSpace(request.QualityLevel))
            {
                if (!QualityLevels.TryParse(request.QualityLevel, out var parsedLevel))
                {
                    return Results.BadRequest(new { error = $"qualityLevel '{request.QualityLevel}' is not supported (use {QualityLevels.Accepted})." });
                }

                qualityLevel = parsedLevel;
            }

            if (request.MaxHeight is not null and (< 16 or > 4320))
            {
                return Results.BadRequest(new { error = "maxHeight must be between 16 and 4320." });
            }

            if (request.AudioStreamIndexes?.Any(index => index < 0) == true ||
                request.SubtitleStreamIndexes?.Any(index => index < 0) == true)
            {
                return Results.BadRequest(new { error = "stream indexes must be non-negative." });
            }

            // A video copy keeps the source picture untouched, so encode-only knobs are contradictory. The
            // reason names which copy it is: a merge that simply omitted the codec never said "copy", and a
            // message quoting one it did not write reads like the request was misunderstood — when what it
            // needs is to be told that naming a codec is what buys the knob.
            var copyReason = isMerge && string.IsNullOrWhiteSpace(request.VideoCodec)
                ? "a merge that names no videoCodec copies the video; name 'h264' or 'hevc' to encode it"
                : "videoCodec is 'copy'";
            if (copyVideo && request.MaxHeight is not null)
            {
                return Results.BadRequest(new { error = $"maxHeight cannot be set when {copyReason}." });
            }

            if (copyVideo && qualityLevel is not null)
            {
                return Results.BadRequest(new { error = $"qualityLevel cannot be set when {copyReason}." });
            }

            // A chosen default needs its explicit (ordered) index list — that's how the engine turns the
            // absolute input index into the output position ffmpeg's -disposition expects — and the chosen
            // index must actually be in that list, otherwise it would clear every default of the type.
            if (DefaultStreamError(request.DefaultAudioStreamIndex, request.AudioStreamIndexes, "audio") is { } audioError)
            {
                return Results.BadRequest(new { error = audioError });
            }

            if (DefaultStreamError(request.DefaultSubtitleStreamIndex, request.SubtitleStreamIndexes, "subtitle") is { } subtitleError)
            {
                return Results.BadRequest(new { error = subtitleError });
            }

            // Subtitles (and their defaults) ride only in Matroska outputs; for other containers BuildArguments
            // drops them, so accepting a subtitle selection would silently do nothing.
            var matroskaOutput = request.OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
            if (!matroskaOutput &&
                (request.SubtitleStreamIndexes is not null || request.DefaultSubtitleStreamIndex is not null))
            {
                return Results.BadRequest(new { error = "subtitle selection is only supported for Matroska (.mkv) outputs." });
            }

            foreach (var input in additionalInputs)
            {
                if (string.IsNullOrWhiteSpace(input.Path))
                {
                    return Results.BadRequest(new { error = "every additional input needs a path." });
                }

                if ((input.AudioStreamIndexes?.Count ?? 0) + (input.SubtitleStreamIndexes?.Count ?? 0) == 0)
                {
                    return Results.BadRequest(new { error = $"additional input '{input.Path}' selects no streams." });
                }

                if (input.AudioStreamIndexes?.Any(index => index < 0) == true ||
                    input.SubtitleStreamIndexes?.Any(index => index < 0) == true)
                {
                    return Results.BadRequest(new { error = "stream indexes must be non-negative." });
                }

                if (!matroskaOutput && input.SubtitleStreamIndexes?.Count > 0)
                {
                    return Results.BadRequest(new { error = "subtitle selection is only supported for Matroska (.mkv) outputs." });
                }
            }

            // A Dolby Vision conversion rides on a video copy and on Matroska at both ends: a re-encode drops
            // the metadata whatever is asked, the enhancement layer is read out of a Matroska input, and
            // mkvmerge writes the result. Whether the input is in fact profile 7 is the engine's to check,
            // since it is the one that probes the input.
            if (!TryParseDolbyVision(request.DolbyVision, out var dolbyVision))
            {
                return Results.BadRequest(new { error = $"dolbyVision '{request.DolbyVision}' is not supported (use 'keep' or 'toProfile81')." });
            }

            if (dolbyVision == DolbyVisionMode.ToProfile81)
            {
                if (!copyVideo)
                {
                    return Results.BadRequest(new { error = "dolbyVision 'toProfile81' needs the video copied: a re-encode drops Dolby Vision whatever is asked. Set videoCodec to 'copy'." });
                }

                if (!matroskaOutput)
                {
                    return Results.BadRequest(new { error = "dolbyVision 'toProfile81' writes Matroska: outputPath must end in .mkv." });
                }

                if (!request.InputPath.TrimEnd().EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "dolbyVision 'toProfile81' reads the enhancement layer out of a Matroska input: inputPath must be an .mkv." });
                }

                if (!DolbyVisionTooling.Available(settings))
                {
                    return Results.BadRequest(new { error = "dolbyVision 'toProfile81' needs dovi_tool and MKVToolNix, which this engine does not have (see GET /hardware)." });
                }
            }

            // An override names a position by (input, absolute index), so the stream it names has to be one
            // the job explicitly maps — the same requirement a chosen default track carries, for the same
            // reason: without an explicit list there is no position to write to.
            if (MetadataOverrideError(request, additionalInputs, matroskaOutput) is { } overrideError)
            {
                return Results.BadRequest(new { error = overrideError });
            }

            // An audio target names a position the same way an override does, so it carries the same
            // requirement: the track has to be one the job maps explicitly.
            if (AudioTargetError(request, additionalInputs, out var audioTargets) is { } audioTargetError)
            {
                return Results.BadRequest(new { error = audioTargetError });
            }

            string inputPath;
            string outputPath;
            try
            {
                inputPath = settings.ResolveMediaPath(request.InputMountLabel, request.InputPath);
                outputPath = settings.ResolveMediaPath(request.OutputMountLabel ?? request.InputMountLabel, request.OutputPath);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            if (!File.Exists(inputPath))
            {
                return Results.BadRequest(new { error = $"input '{request.InputPath}' does not exist on the media mount." });
            }

            if (string.Equals(inputPath, outputPath, PathComparison))
            {
                return Results.BadRequest(new { error = "outputPath must differ from inputPath." });
            }

            // Each additional input resolves the same way the primary does, defaulting to its mount, and
            // must exist and differ from the output — the same checks, so the same failures read alike.
            var resolvedInputs = new List<AdditionalInput>(additionalInputs.Count);
            foreach (var input in additionalInputs)
            {
                string resolved;
                try
                {
                    resolved = settings.ResolveMediaPath(input.MountLabel ?? request.InputMountLabel, input.Path);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }

                if (!File.Exists(resolved))
                {
                    return Results.BadRequest(new { error = $"input '{input.Path}' does not exist on the media mount." });
                }

                if (string.Equals(resolved, outputPath, PathComparison))
                {
                    return Results.BadRequest(new { error = "outputPath must differ from every input." });
                }

                resolvedInputs.Add(new AdditionalInput(resolved, input.AudioStreamIndexes, input.SubtitleStreamIndexes));
            }

            var jobRequest = new TranscodeJobRequest(
                inputPath,
                outputPath,
                codec,
                hardware,
                qualityLevel,
                copyVideo,
                request.MaxHeight,
                request.AudioStreamIndexes,
                request.SubtitleStreamIndexes,
                request.DefaultAudioStreamIndex,
                request.DefaultSubtitleStreamIndex,
                resolvedInputs.Count > 0 ? resolvedInputs : null,
                request.MetadataOverrides?.Select(o => new StreamMetadataOverride(o.Input, o.StreamIndex, o.Language, o.Title)).ToList(),
                audioTargets,
                DolbyVision: dolbyVision);

            // The engine refuses what only the probed input can tell — a Dolby Vision conversion of a source
            // that is not profile 7 — as an ArgumentException, which is the same 400 as every rule above.
            try
            {
                var descriptor = await engine.CreateAsync(jobRequest, ct);
                return Results.Ok(descriptor);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // Inspection, not a job: it answers in the response rather than creating something to poll, so a
        // consumer can read a file's streams without shipping ffprobe itself. One file per call — ffprobe
        // dominates the cost, so batching would buy partial-failure semantics for a sliver of the time.
        // [FromServices] on the inspector: without it, minimal-API parameter inference has to ask the
        // container whether IMediaInspector is a service or the request body, which forces every host that
        // maps these endpoints — including tests that only exercise /jobs — to register it.
        app.MapPost("/probe", async (
            ProbeRequest request,
            [FromServices] IMediaInspector inspector,
            TranscodeEngineSettings settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path is required." });
            }

            string absolutePath;
            try
            {
                absolutePath = settings.ResolveMediaPath(request.MountLabel, request.Path);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            if (!File.Exists(absolutePath))
            {
                return Results.BadRequest(new { error = $"'{request.Path}' does not exist on the media mount." });
            }

            var result = await inspector.InspectAsync(absolutePath, ct);
            return result is null
                ? Results.BadRequest(new { error = $"'{request.Path}' could not be probed." })
                : Results.Ok(result);
        });

        app.MapGet("/jobs", (ITranscodeEngine engine) => Results.Ok(engine.GetAllSnapshots()));

        app.MapGet("/jobs/{jobId}", (string jobId, ITranscodeEngine engine) =>
            engine.GetSnapshot(jobId) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());

        app.MapPost("/jobs/{jobId}/cancel", async (string jobId, ITranscodeEngine engine, CancellationToken ct) =>
        {
            await engine.CancelAsync(jobId, ct);
            return Results.NoContent();
        });

        app.MapDelete("/jobs/{jobId}", async (string jobId, ITranscodeEngine engine, CancellationToken ct, bool deleteOutput = false) =>
        {
            await engine.RemoveAsync(jobId, deleteOutput, ct);
            return Results.NoContent();
        });

        app.MapGet("/events", async (HttpContext context, TranscodeEventStream stream, CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var (id, reader) = stream.Subscribe();
            try
            {
                // Flush a comment immediately so EventSource fires `open` and any proxy commits the response
                // headers even when there are no jobs — otherwise an idle stream sends no bytes and the
                // client hangs "connecting" until a proxy idle-timeout drops it.
                await context.Response.WriteAsync(": connected\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                // Emit a keepalive comment on idle so proxy/browser idle-timeouts don't drop a live but
                // quiet stream (no jobs running means no progress ticks to carry the connection).
                using var keepalive = new PeriodicTimer(KeepaliveInterval);
                var keepaliveTick = keepalive.WaitForNextTickAsync(ct).AsTask();
                var nextEvent = reader.WaitToReadAsync(ct).AsTask();
                while (true)
                {
                    if (await Task.WhenAny(keepaliveTick, nextEvent) == keepaliveTick)
                    {
                        await keepaliveTick;
                        await context.Response.WriteAsync(": keepalive\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        keepaliveTick = keepalive.WaitForNextTickAsync(ct).AsTask();
                        continue;
                    }

                    if (!await nextEvent)
                    {
                        break; // The subscription channel completed (unsubscribed).
                    }

                    while (reader.TryRead(out var evt))
                    {
                        var data = JsonSerializer.Serialize(evt, EventJson);
                        await context.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
                    }

                    await context.Response.Body.FlushAsync(ct);
                    nextEvent = reader.WaitToReadAsync(ct).AsTask();
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            finally
            {
                stream.Unsubscribe(id);
            }
        });
    }

    /// <summary>
    /// Validates and creates an extraction: one input, one output file per named stream. Everything a
    /// composed output needs is refused here rather than ignored — an extraction has no picture to encode,
    /// scale or accelerate, and no single output to select tracks into.
    /// <para>
    /// The check that a named stream exists and is extractable lives in the engine, not here: it needs the
    /// input's streams, and the engine already probes the input when it creates a job. Its refusals arrive as
    /// <see cref="ArgumentException"/> and become the same 400 as everything else, so a caller cannot tell
    /// which side of the line a rule sat on.
    /// </para>
    /// </summary>
    private static async Task<IResult> CreateExtractionAsync(
        CreateJobRequest request,
        IReadOnlyList<OutputRequest> outputs,
        ITranscodeEngine engine,
        TranscodeEngineSettings settings,
        CancellationToken cancellationToken)
    {
        if (ExtractionConflictError(request) is { } conflict)
        {
            return Results.BadRequest(new { error = conflict });
        }

        string inputPath;
        try
        {
            inputPath = settings.ResolveMediaPath(request.InputMountLabel, request.InputPath);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        if (!File.Exists(inputPath))
        {
            return Results.BadRequest(new { error = $"input '{request.InputPath}' does not exist on the media mount." });
        }

        var resolved = new List<ExtractionOutput>(outputs.Count);
        var claimed = new HashSet<string>(PathComparer);
        foreach (var output in outputs)
        {
            // A JSON array may hold a literal null, which the non-nullable element type does not prevent.
            // Reading through it would be a 500 for what is an ordinary malformed request.
            if (output is null)
            {
                return Results.BadRequest(new { error = "every entry in outputs must be an object with a path." });
            }

            if (string.IsNullOrWhiteSpace(output.Path))
            {
                return Results.BadRequest(new { error = "every output needs a path." });
            }

            if (output.StreamIndex < 0)
            {
                return Results.BadRequest(new { error = "stream indexes must be non-negative." });
            }

            if (!TryParseExtractionCodec(output.Codec, out var codec))
            {
                return Results.BadRequest(new { error = $"output codec '{output.Codec}' is not supported (use 'copy', 'srt', 'ass' or 'webvtt')." });
            }

            string path;
            try
            {
                // outputMountLabel means the same thing here as it does for a composed output — the mount
                // the results are written to — so it is the default for every entry that names none. It fell
                // through to the input's mount before, which silently ignored a field the caller had set.
                path = settings.ResolveMediaPath(
                    output.MountLabel ?? request.OutputMountLabel ?? request.InputMountLabel, output.Path);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            if (string.Equals(path, inputPath, PathComparison))
            {
                return Results.BadRequest(new { error = "every output must differ from inputPath." });
            }

            // Two outputs at one path would race each other's publish and leave whichever finished last,
            // silently losing a track the caller asked for.
            if (!claimed.Add(path))
            {
                return Results.BadRequest(new { error = $"two outputs write to '{output.Path}'." });
            }

            resolved.Add(new ExtractionOutput(path, output.StreamIndex, codec, Clean(output.Language), Clean(output.Title)));
        }

        // The video codec and quality level are structurally required by the request record but mean nothing
        // here — TranscodeJobRequest.IsExtraction is what every consumer of it branches on.
        var jobRequest = new TranscodeJobRequest(
            inputPath,
            OutputPath: null,
            TranscodeVideoCodec.Hevc,
            TranscodeHardware.None,
            QualityLevel: null,
            Outputs: resolved);

        try
        {
            var descriptor = await engine.CreateAsync(jobRequest, cancellationToken);
            return Results.Ok(descriptor);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>
    /// The composed-output fields an extraction cannot carry. Each is refused rather than ignored, for the
    /// reason the encode-only knobs are refused on a video copy: accepting a field that cannot take effect
    /// reports success for a job that does something other than what was asked.
    /// </summary>
    private static string? ExtractionConflictError(CreateJobRequest request) => request switch
    {
        { OutputPath: { } path } when !string.IsNullOrWhiteSpace(path) =>
            "outputPath and outputs are mutually exclusive: an extraction writes each stream to its own file and composes none.",
        { VideoCodec: { } codec } when !string.IsNullOrWhiteSpace(codec) =>
            "videoCodec cannot be set on an extraction: it produces no video output.",
        { MaxHeight: not null } =>
            "maxHeight cannot be set on an extraction: it produces no video output.",
        { QualityLevel: { } level } when !string.IsNullOrWhiteSpace(level) =>
            "qualityLevel cannot be set on an extraction: it produces no video output.",
        // Nothing is decoded or encoded, so there is nothing to accelerate. 'auto' is accepted and inert
        // because it is what a client sends by default, and failing the ordinary call over a field that means
        // nothing here would be gratuitous.
        { HardwareAcceleration: { } hardware } when !string.IsNullOrWhiteSpace(hardware) &&
            !string.Equals(hardware.Trim(), "auto", StringComparison.OrdinalIgnoreCase) =>
            "hardwareAcceleration cannot be set on an extraction: nothing is decoded or encoded. Omit it or pass 'auto'.",
        { AdditionalInputs.Count: > 0 } =>
            "additionalInputs cannot be set on an extraction: it reads one input and composes nothing.",
        { AudioStreamIndexes: not null } or { SubtitleStreamIndexes: not null } =>
            "audioStreamIndexes and subtitleStreamIndexes select tracks into a composed output; an extraction names its streams in outputs.",
        { DefaultAudioStreamIndex: not null } or { DefaultSubtitleStreamIndex: not null } =>
            "a default track belongs to a composed output; an extraction writes one stream per file.",
        { AudioTargets.Count: > 0 } =>
            "audioTargets re-encode tracks of a composed output; an extraction copies its streams.",
        { MetadataOverrides.Count: > 0 } =>
            "metadataOverrides address a composed output's stream positions; set language and title on the output itself.",
        { DolbyVision: { } dolbyVision } when !string.IsNullOrWhiteSpace(dolbyVision) =>
            "dolbyVision rewrites a composed output's picture; an extraction copies its streams.",
        _ => null,
    };

    /// <summary>The two words <c>dolbyVision</c> accepts. Absent means keep, so a caller written before the
    /// field existed gets exactly the job it always got.</summary>
    private static bool TryParseDolbyVision(string? raw, out DolbyVisionMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "keep":
                mode = DolbyVisionMode.Keep;
                return true;
            case "toprofile81" or "to-profile-81" or "to-profile-8.1" or "8.1":
                mode = DolbyVisionMode.ToProfile81;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>Whitespace is not a value: argument construction emits a field only when it has content, so
    /// carrying "" through would claim a metadata write the output never gets.</summary>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseExtractionCodec(string? raw, out ExtractionCodec codec)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "copy":
                codec = ExtractionCodec.Copy; // default — an extraction takes the packets as they are
                return true;
            case "srt" or "subrip":
                codec = ExtractionCodec.Srt;
                return true;
            case "ass" or "ssa":
                codec = ExtractionCodec.Ass;
                return true;
            case "webvtt" or "vtt":
                codec = ExtractionCodec.WebVtt;
                return true;
            default:
                codec = default;
                return false;
        }
    }

    /// <summary>
    /// Validates every metadata override: it must name a real input, carry at least one value to write, and
    /// address a stream the job explicitly maps — an override against an unlisted stream has no output
    /// position, exactly as a chosen default track would not. Returns an error message, or null when valid.
    /// </summary>
    private static string? MetadataOverrideError(
        CreateJobRequest request,
        IReadOnlyList<AdditionalInputRequest> additionalInputs,
        bool matroskaOutput)
    {
        var overrides = request.MetadataOverrides ?? [];
        var seen = new HashSet<(int Input, int StreamIndex)>();
        foreach (var entry in overrides)
        {
            // Whitespace is not a value: argument construction emits a field only when it has content, so
            // accepting "" here would report success for a job that changes nothing.
            if (string.IsNullOrWhiteSpace(entry.Language) && string.IsNullOrWhiteSpace(entry.Title))
            {
                return "a metadata override must set a language or a title.";
            }

            if (entry.Input < 0 || entry.Input > additionalInputs.Count)
            {
                return $"metadata override names input {entry.Input}, which this job does not have.";
            }

            if (entry.StreamIndex < 0)
            {
                return "stream indexes must be non-negative.";
            }

            // Two overrides for one stream cannot both be applied, and picking one silently would discard
            // the caller's other instruction.
            if (!seen.Add((entry.Input, entry.StreamIndex)))
            {
                return $"stream {entry.StreamIndex} of input {entry.Input} has more than one metadata override.";
            }

            var (audio, subtitles) = entry.Input == 0
                ? (request.AudioStreamIndexes, request.SubtitleStreamIndexes)
                : (additionalInputs[entry.Input - 1].AudioStreamIndexes, additionalInputs[entry.Input - 1].SubtitleStreamIndexes);

            var isAudio = audio?.Contains(entry.StreamIndex) == true;
            var isSubtitle = matroskaOutput && subtitles?.Contains(entry.StreamIndex) == true;
            if (!isAudio && !isSubtitle)
            {
                return $"metadata override for stream {entry.StreamIndex} of input {entry.Input} must name a stream the job maps explicitly.";
            }

            // Output positions are assigned in map order, and a null primary selection is a single "copy
            // every stream of this type" mapping that expands to however many the file holds. An appended
            // track sitting after it therefore has no position this app can compute, and writing metadata
            // against the assumed one would relabel a primary track instead.
            if (entry.Input > 0)
            {
                if (isAudio && request.AudioStreamIndexes is null)
                {
                    return "audioStreamIndexes must be listed explicitly when a metadata override targets an appended audio track.";
                }

                if (isSubtitle && request.SubtitleStreamIndexes is null)
                {
                    return "subtitleStreamIndexes must be listed explicitly when a metadata override targets an appended subtitle track.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates every audio target and, on success, hands back the resolved list. A target re-encodes one
    /// mapped track, so it addresses a stream exactly as a metadata override does and carries the same
    /// requirement — the track must be explicitly mapped, because a "copy every stream of this type"
    /// selection expands to however many the file holds and yields no position to attach <c>-c:a:N</c> to.
    /// Returns an error message, or null when valid.
    /// </summary>
    private static string? AudioTargetError(
        CreateJobRequest request,
        IReadOnlyList<AdditionalInputRequest> additionalInputs,
        out List<AudioTarget>? resolved)
    {
        resolved = null;
        var targets = request.AudioTargets ?? [];
        if (targets.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<(int Input, int StreamIndex)>();
        var result = new List<AudioTarget>(targets.Count);
        foreach (var entry in targets)
        {
            if (!TryParseAudioCodec(entry.Codec, out var codec))
            {
                return $"audio target codec '{entry.Codec}' is not supported (use 'eac3' or 'ac3').";
            }

            if (entry.Bitrate is not null and (< 32 or > 1536))
            {
                return "audio target bitrate must be between 32 and 1536 kbps.";
            }

            if (entry.Input < 0 || entry.Input > additionalInputs.Count)
            {
                return $"audio target names input {entry.Input}, which this job does not have.";
            }

            if (entry.StreamIndex < 0)
            {
                return "stream indexes must be non-negative.";
            }

            // Two targets for one track cannot both be applied, and silently picking one would discard the
            // caller's other instruction.
            if (!seen.Add((entry.Input, entry.StreamIndex)))
            {
                return $"stream {entry.StreamIndex} of input {entry.Input} has more than one audio target.";
            }

            // Every position after an un-enumerated primary selection shifts by however many audio streams
            // the file turns out to hold, so no target anywhere in the job is addressable without the list.
            if (request.AudioStreamIndexes is null)
            {
                return "audioStreamIndexes must be listed explicitly when a job re-encodes an audio track.";
            }

            var streams = entry.Input == 0
                ? request.AudioStreamIndexes
                : additionalInputs[entry.Input - 1].AudioStreamIndexes;
            if (streams?.Contains(entry.StreamIndex) != true)
            {
                return $"audio target for stream {entry.StreamIndex} of input {entry.Input} must name a stream the job maps explicitly.";
            }

            result.Add(new AudioTarget(entry.Input, entry.StreamIndex, codec, entry.Bitrate));
        }

        resolved = result;
        return null;
    }

    private static bool TryParseAudioCodec(string? raw, out TranscodeAudioCodec codec)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "eac3" or "e-ac-3" or "ddp":
                codec = TranscodeAudioCodec.Eac3;
                return true;
            case "ac3" or "ac-3":
                codec = TranscodeAudioCodec.Ac3;
                return true;
            default:
                codec = default;
                return false;
        }
    }

    /// <summary>Validates a chosen default track: it requires the explicit index list (to map the absolute
    /// index to an output position) and must be present in it. Returns an error message, or null when valid
    /// or unset.</summary>
    private static string? DefaultStreamError(int? defaultIndex, IReadOnlyList<int>? indexes, string kind)
    {
        if (defaultIndex is not { } index)
        {
            return null;
        }

        if (indexes is null)
        {
            return $"{kind}StreamIndexes must be set when default{Capitalize(kind)}StreamIndex is given.";
        }

        return indexes.Contains(index)
            ? null
            : $"default{Capitalize(kind)}StreamIndex must be one of {kind}StreamIndexes.";
    }

    private static string Capitalize(string value) => $"{char.ToUpperInvariant(value[0])}{value[1..]}";

    private static bool TryParseCodec(string? raw, out TranscodeVideoCodec codec)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "":
            case "hevc" or "h265" or "x265":
                codec = TranscodeVideoCodec.Hevc; // default
                return true;
            case "h264" or "avc" or "x264":
                codec = TranscodeVideoCodec.H264;
                return true;
            default:
                codec = default;
                return false;
        }
    }

    private static bool TryParseHardware(string? raw, out TranscodeHardware hardware)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            hardware = TranscodeHardware.Auto; // default
            return true;
        }

        var parsed = TranscodeEngineSettings.ParseHardware(raw);
        hardware = parsed ?? TranscodeHardware.Auto;
        return parsed is not null;
    }
}
