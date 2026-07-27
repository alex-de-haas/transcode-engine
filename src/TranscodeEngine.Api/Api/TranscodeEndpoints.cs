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

    public static void MapTranscodeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/jobs", async (CreateJobRequest request, ITranscodeEngine engine, TranscodeEngineSettings settings, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.InputPath))
            {
                return Results.BadRequest(new { error = "inputPath is required." });
            }

            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                return Results.BadRequest(new { error = "outputPath is required." });
            }

            // "copy" remuxes the video untouched; the codec/hardware/crf/height knobs are then irrelevant.
            // Naming additional inputs makes the job a merge, which is a stream copy by definition — so it
            // implies the same thing and rejects the same knobs.
            var additionalInputs = request.AdditionalInputs ?? [];
            var isMerge = additionalInputs.Count > 0;
            var copyVideo = isMerge ||
                string.Equals(request.VideoCodec?.Trim(), "copy", StringComparison.OrdinalIgnoreCase);

            var codec = TranscodeVideoCodec.Hevc;
            if (!copyVideo && !TryParseCodec(request.VideoCodec, out codec))
            {
                return Results.BadRequest(new { error = $"videoCodec '{request.VideoCodec}' is not supported (use 'h264', 'hevc' or 'copy')." });
            }

            if (!TryParseHardware(request.HardwareAcceleration, out var hardware))
            {
                return Results.BadRequest(new { error = $"hardwareAcceleration '{request.HardwareAcceleration}' is not supported (use 'auto', 'vaapi', 'videotoolbox', 'amf' or 'none')." });
            }

            if (request.Crf is < 0 or > 51)
            {
                return Results.BadRequest(new { error = "crf must be between 0 and 51." });
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

            // A video copy keeps the source picture untouched, so encode-only knobs are contradictory.
            if (copyVideo && request.MaxHeight is not null)
            {
                return Results.BadRequest(new { error = "maxHeight cannot be set when videoCodec is 'copy'." });
            }

            if (copyVideo && request.Crf is not null)
            {
                return Results.BadRequest(new { error = "crf cannot be set when videoCodec is 'copy'." });
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

            // An override names a position by (input, absolute index), so the stream it names has to be one
            // the job explicitly maps — the same requirement a chosen default track carries, for the same
            // reason: without an explicit list there is no position to write to.
            if (MetadataOverrideError(request, additionalInputs, matroskaOutput) is { } overrideError)
            {
                return Results.BadRequest(new { error = overrideError });
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

            if (string.Equals(inputPath, outputPath, StringComparison.Ordinal))
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

                if (string.Equals(resolved, outputPath, StringComparison.Ordinal))
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
                request.Crf,
                copyVideo,
                request.MaxHeight,
                request.AudioStreamIndexes,
                request.SubtitleStreamIndexes,
                request.DefaultAudioStreamIndex,
                request.DefaultSubtitleStreamIndex,
                resolvedInputs.Count > 0 ? resolvedInputs : null,
                request.MetadataOverrides?.Select(o => new StreamMetadataOverride(o.Input, o.StreamIndex, o.Language, o.Title)).ToList());
            var descriptor = await engine.CreateAsync(jobRequest, ct);
            return Results.Ok(descriptor);
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
