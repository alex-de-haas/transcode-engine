using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Probing;

/// <summary>Inspects a media file and maps the result into this API's normalized vocabulary.</summary>
public interface IMediaInspector
{
    /// <summary>Probes an absolute path. Returns null when the file could not be read as media.</summary>
    Task<ProbeResponse?> InspectAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>ffprobe</c> and translates its JSON into <see cref="ProbeResponse"/>. The translation is
/// deliberate rather than a passthrough: a consumer compares these fields against its own header parser,
/// so this app — not ffprobe's schema — owns the vocabulary.
/// </summary>
public sealed class FfprobeMediaInspector(
    TranscodeEngineSettings settings,
    ILogger<FfprobeMediaInspector> logger) : IMediaInspector
{
    /// <summary>Matches the bound <c>CreateAsync</c> already puts on its duration probe: a FIFO, a special
    /// file or a blocked read must fail the request rather than hang it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<ProbeResponse?> InspectAsync(string absolutePath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        var probeToken = timeoutCts.Token;

        string json;
        try
        {
            json = await RunAsync(absolutePath, probeToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Probing {Input} timed out after {Timeout}.", absolutePath, Timeout);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not probe {Input}.", absolutePath);
            return null;
        }

        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            return Map(json, absolutePath);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "ffprobe returned output for {Input} that could not be read.", absolutePath);
            return null;
        }
    }

    private async Task<string> RunAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(settings.FfprobePath)
        {
            RedirectStandardOutput = true,
            // -v quiet silences stderr; leaving it un-redirected avoids a full stderr pipe deadlocking
            // ffprobe, the same reasoning the duration probe in FfmpegTranscodeEngine follows.
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            absolutePath,
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start.");
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? stdout : string.Empty;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort: it may have exited between the check and the kill.
        }
    }

    internal static ProbeResponse? Map(string json, string absolutePath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        // ffprobe reports the demuxer's format list ("matroska,webm"); the real container is the extension,
        // falling back to the first listed name for a file that has none.
        var extension = System.IO.Path.GetExtension(absolutePath).TrimStart('.').ToLowerInvariant();
        var container = extension.Length > 0
            ? extension
            : (String(format, "format_name")?.Split(',').FirstOrDefault()?.Trim() ?? string.Empty);

        var mapped = streams.EnumerateArray().Select(MapStream).ToList();
        return new ProbeResponse(
            container,
            Double(format, "duration"),
            Int(format, "bit_rate"),
            Long(format, "size") ?? 0,
            mapped);
    }

    private static ProbedStreamInfo MapStream(JsonElement stream)
    {
        var tags = stream.TryGetProperty("tags", out var t) ? t : default;
        var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
        var kind = String(stream, "codec_type") switch
        {
            "video" => ProbedStreamKind.Video,
            "audio" => ProbedStreamKind.Audio,
            "subtitle" => ProbedStreamKind.Subtitle,
            _ => ProbedStreamKind.Other,
        };

        return new ProbedStreamInfo(
            Int(stream, "index") ?? 0,
            kind,
            String(stream, "codec_name"),
            String(stream, "profile"),
            Language(tags),
            // MP4 keeps a track's name in udta/name, which ffprobe surfaces as the "name" tag rather than
            // "title"; Matroska uses "title". Take whichever the file carries.
            String(tags, "title") ?? String(tags, "name"),
            Flag(disposition, "default"),
            Flag(disposition, "forced"),
            Bitrate(stream, tags),
            Int(stream, "width"),
            Int(stream, "height"),
            FrameRate(stream),
            Int(stream, "bits_per_raw_sample"),
            kind == ProbedStreamKind.Video ? Hdr(stream) : HdrFormat.Unknown,
            Int(stream, "channels"),
            Int(stream, "sample_rate"),
            kind == ProbedStreamKind.Video ? DolbyVision(stream) : null);
    }

    /// <summary>
    /// The Dolby Vision configuration record, from the <c>side_data_list</c> entry ffprobe types as
    /// <c>DOVI configuration record</c>. Read field by field rather than inferred: the profile, the layer
    /// flags and the base-layer compatibility id are exactly what a consumer needs to tell a disc's dual-layer
    /// profile 7 from a single-layer 8.1, and nothing about them can be guessed from the transfer function.
    /// An entry without a profile is not a record.
    /// </summary>
    private static DolbyVisionInfo? DolbyVision(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in list.EnumerateArray())
        {
            var type = String(entry, "side_data_type") ?? string.Empty;
            if (!type.Contains("dovi", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("dolby vision", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Int(entry, "dv_profile") is not { } profile)
            {
                continue;
            }

            return new DolbyVisionInfo(
                profile,
                Int(entry, "dv_level") ?? 0,
                Int(entry, "dv_bl_signal_compatibility_id") ?? 0,
                Flag(entry, "rpu_present_flag"),
                Flag(entry, "el_present_flag"),
                Flag(entry, "bl_present_flag"));
        }

        return null;
    }

    /// <summary>
    /// A stream's own bitrate. <c>bit_rate</c> is the direct answer, but Matroska stores no per-track rate
    /// and ffprobe leaves the field out — so on exactly the remuxes worth measuring it is absent, and
    /// mkvmerge's <c>BPS</c> statistics tag is what the file actually carries. A stream with neither answers
    /// null: the alternative is a share of the file's overall rate, which is a guess.
    /// </summary>
    private static int? Bitrate(JsonElement stream, JsonElement tags) =>
        Positive(Int(stream, "bit_rate")) ?? Positive(BitsPerSecondTag(tags));

    /// <summary>
    /// mkvmerge's per-track <c>BPS</c> tag. ffmpeg appends the tag's language when the file sets one, so a
    /// remux commonly spells it <c>BPS-eng</c> and the plain name is missing; both are read, and a track
    /// carrying both states the same figure twice.
    /// </summary>
    private static int? BitsPerSecondTag(JsonElement tags)
    {
        if (tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if ((tag.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase) ||
                 tag.Name.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase)) &&
                Int(tags, tag.Name) is { } bitrate)
            {
                return bitrate;
            }
        }

        return null;
    }

    /// <summary>A zero or negative rate is ffprobe declining to answer, not a measurement.</summary>
    private static int? Positive(int? value) => value > 0 ? value : null;

    /// <summary>"und" is ffprobe saying it has no language, not a language.</summary>
    private static string? Language(JsonElement tags) =>
        String(tags, "language") is { Length: > 0 } value &&
        !value.Equals("und", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    /// <summary>
    /// The transfer function decides HDR; a Dolby Vision configuration record on the stream outranks it.
    /// HDR10+ is only claimed when the stream carries SMPTE 2094-40 side data — a file whose dynamic layer
    /// lives in frame side data reads as plain HDR10, under-reporting rather than guessing.
    /// </summary>
    private static HdrFormat Hdr(JsonElement stream)
    {
        var sideData = SideDataTypes(stream);
        if (sideData.Any(type => type.Contains("dovi", StringComparison.OrdinalIgnoreCase) ||
                                 type.Contains("dolby vision", StringComparison.OrdinalIgnoreCase)))
        {
            return HdrFormat.DolbyVision;
        }

        return String(stream, "color_transfer") switch
        {
            "smpte2084" => sideData.Any(type => type.Contains("2094", StringComparison.OrdinalIgnoreCase) ||
                                                type.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
                ? HdrFormat.Hdr10Plus
                : HdrFormat.Hdr10,
            "arib-std-b67" => HdrFormat.Hlg,
            null or "" or "unknown" => HdrFormat.Unknown,
            _ => HdrFormat.Sdr,
        };
    }

    private static List<string> SideDataTypes(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. list.EnumerateArray().Select(entry => String(entry, "side_data_type") ?? string.Empty)];
    }

    /// <summary>ffprobe writes the frame rate as a rational string ("24000/1001"); 0/0 means unknown.</summary>
    private static double? FrameRate(JsonElement stream)
    {
        var raw = String(stream, "avg_frame_rate") ?? String(stream, "r_frame_rate");
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0 || numerator == 0)
        {
            return null;
        }

        return numerator / denominator;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>ffprobe writes numbers as strings in most places and as numbers in a few; accept both.</summary>
    private static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number : null,
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static int? Int(JsonElement element, string name) =>
        Long(element, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static double? Double(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static bool Flag(JsonElement element, string name) => Long(element, name) == 1;
}
