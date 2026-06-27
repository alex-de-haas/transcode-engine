using System.Text.Json;
using TranscodeEngine.Api.Realtime;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Api;

/// <summary>Maps the control API: create/list/inspect/cancel/remove jobs, plus the SSE event stream.
/// Engine records (<see cref="JobDescriptor"/>/<see cref="JobSnapshot"/>) are returned directly.</summary>
public static class TranscodeEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

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
            var copyVideo = string.Equals(request.VideoCodec?.Trim(), "copy", StringComparison.OrdinalIgnoreCase);

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
            if (!request.OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) &&
                (request.SubtitleStreamIndexes is not null || request.DefaultSubtitleStreamIndex is not null))
            {
                return Results.BadRequest(new { error = "subtitle selection is only supported for Matroska (.mkv) outputs." });
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
                request.DefaultSubtitleStreamIndex);
            var descriptor = await engine.CreateAsync(jobRequest, ct);
            return Results.Ok(descriptor);
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
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    var data = JsonSerializer.Serialize(evt, EventJson);
                    await context.Response.WriteAsync($"event: {evt.Type}\ndata: {data}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
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
