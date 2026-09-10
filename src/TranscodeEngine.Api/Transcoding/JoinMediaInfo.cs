using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TranscodeEngine.Api.Transcoding;

/// <summary>A fresh, strict probe for concatenation. Unknown compatibility is a refusal.</summary>
internal sealed record JoinMediaInfo(string Path, long Size, DateTime LastWrite, double Duration,
    double Start, JsonElement[] Streams, JsonElement[] Chapters)
{
    private static string Value(JsonElement node, string key) => node.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static string Tag(JsonElement node, string key) => node.TryGetProperty("tags", out var tags) ? Value(tags, key) : "";
    private static double Number(JsonElement node, string key) => double.TryParse(Value(node, key),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : double.NaN;

    public static JoinMediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format) || !root.TryGetProperty("streams", out var streams))
            throw new ArgumentException("The video could not be inspected for joining.");
        var duration = Number(format, "duration");
        if (!(duration > 0)) throw new ArgumentException("A part has no reliable duration. Refresh or repair the file before joining.");
        var start = Number(format, "start_time");
        // Matroska's Duration is the end timestamp, including a positive Segment start offset.
        // Other containers are refused here rather than guessing whether duration includes that offset.
        if (double.IsFinite(start) && start > 0.05)
        {
            if (!Value(format, "format_name").Contains("matroska", StringComparison.Ordinal))
                throw new ArgumentException("Joining a nonzero start timestamp currently requires Matroska inputs.");
            duration -= start;
            if (!(duration > 0)) throw new ArgumentException("A part has invalid start/duration timing.");
        }
        var info = new FileInfo(path);
        return new(path, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : default,
            duration, double.IsFinite(start) ? start : 0, streams.EnumerateArray().Select(x => x.Clone()).ToArray(),
            root.TryGetProperty("chapters", out var chapters) ? chapters.EnumerateArray().Select(x => x.Clone()).ToArray() : []);
    }

    public void RequireUnchanged()
    {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length != Size || file.LastWriteTimeUtc != LastWrite)
            throw new ArgumentException("A selected part changed after inspection. Submit the join again.");
    }

    public static void Validate(JoinMediaInfo first, JoinMediaInfo second)
    {
        foreach (var part in new[] { first, second })
        {
            if (part.Streams.Count(s => Value(s, "codec_type") == "video") != 1)
                throw new ArgumentException("Joining requires exactly one video track in each part.");
            foreach (var stream in part.Streams)
            {
                var kind = Value(stream, "codec_type");
                var codec = Value(stream, "codec_name");
                var supported = kind switch
                {
                    "video" => codec is "h264" or "hevc" or "mpeg4" or "mpeg2video",
                    "audio" => codec is "aac" or "ac3" or "mp3" or "flac" or "pcm_s16le",
                    "subtitle" => codec is "subrip" or "ass" or "ssa" or "webvtt",
                    "attachment" => codec is "ttf" or "otf",
                    _ => false,
                };
                if (!supported) throw new ArgumentException($"Joining {kind} codec '{codec}' is not supported yet; no tracks were changed.");
                if (kind == "video" && (Value(stream, "pix_fmt") is not ("yuv420p" or "yuv422p" or "yuv444p") ||
                    Value(stream, "color_transfer") is "smpte2084" or "arib-std-b67" ||
                    Value(stream, "side_data_list").Contains("DOVI", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("HDR, Dolby Vision and high-bit-depth joining are not verified yet. The original parts are retained.");
                if (kind is not "attachment" && (Value(stream, "time_base").Length == 0 || Value(stream, "codec_name").Length == 0))
                    throw new ArgumentException("A track's timing or codec is unknown; the parts cannot be joined safely.");
                if (Value(stream, "extradata_size") is not ("" or "0") && Value(stream, "extradata_hash").Length == 0)
                    throw new ArgumentException("A track's codec configuration could not be checked.");
            }
        }
        if (first.Streams.Length != second.Streams.Length)
            throw new ArgumentException("The parts have different numbers of tracks. Match their tracks before joining.");
        string[] fields = ["codec_type", "codec_name", "profile", "codec_tag_string", "time_base", "width", "height",
            "pix_fmt", "sample_aspect_ratio", "r_frame_rate", "sample_fmt", "sample_rate", "channels", "channel_layout",
            "bits_per_raw_sample", "extradata_hash", "color_range", "color_space", "color_transfer", "color_primaries"];
        for (var i = 0; i < first.Streams.Length; i++)
        {
            var a = first.Streams[i]; var b = second.Streams[i];
            foreach (var field in fields)
                if (Value(a, field) != Value(b, field))
                    throw new ArgumentException($"Track {i + 1} differs between parts ({field}: '{Value(a, field)}' / '{Value(b, field)}'). Joining requires matching tracks.");
            foreach (var tag in new[] { "language", "title", "filename", "mimetype" })
                if (Tag(a, tag) != Tag(b, tag))
                    throw new ArgumentException($"Track {i + 1} has different {tag} tags. Confirm the tracks correspond before joining.");
            if (Value(a, "disposition") != Value(b, "disposition"))
                throw new ArgumentException($"Track {i + 1} has different default/forced flags between parts.");
        }
    }

    public static void ValidateOutput(JoinMediaInfo[] parts, JoinMediaInfo output)
    {
        if (Math.Abs(output.Duration - parts.Sum(part => part.Duration)) > 0.5 || output.Size == 0)
            throw new ArgumentException("The joined file has an unexpected duration; it was not published.");
        var expected = parts[0].Streams;
        if (expected.Length != output.Streams.Length || expected.Where((s, i) =>
            Value(s, "codec_name") != Value(output.Streams[i], "codec_name") ||
            Tag(s, "language") != Tag(output.Streams[i], "language") ||
            Tag(s, "title") != Tag(output.Streams[i], "title")).Any())
            throw new ArgumentException("The joined file did not preserve its tracks; it was not published.");
        if (output.Chapters.Length != parts.Sum(part => part.Chapters.Length))
            throw new ArgumentException("The joined file did not preserve its chapters; it was not published.");
    }

    public static string ConcatList(IReadOnlyList<string> paths, JoinMediaInfo[] parts)
    {
        var text = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < paths.Count; i++)
        {
            if (paths[i].IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Joining filenames containing line breaks is not supported.");
            text.Append("file '").Append(paths[i].Replace("'", "'\\''")).Append("'\nduration ")
                .Append(parts[i].Duration.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    public static string Metadata(JoinMediaInfo[] parts)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        double offset = 0;
        foreach (var part in parts)
        {
            foreach (var chapter in part.Chapters)
            {
                var start = Number(chapter, "start_time") - part.Start;
                var end = Number(chapter, "end_time") - part.Start;
                if (!double.IsFinite(start) || !double.IsFinite(end) || start < -0.05 || end <= start || end > part.Duration + 0.05)
                    throw new ArgumentException("A part has invalid chapter timing; repair it before joining.");
                text.Append("[CHAPTER]\nTIMEBASE=1/1000000\nSTART=").Append((long)Math.Round((offset + Math.Max(0, start)) * 1_000_000))
                    .Append("\nEND=").Append((long)Math.Round((offset + end) * 1_000_000)).Append('\n');
                if (chapter.TryGetProperty("tags", out var tags))
                    foreach (var tag in tags.EnumerateObject())
                        text.Append(Escape(tag.Name)).Append('=').Append(Escape(tag.Value.ToString())).Append('\n');
            }
            offset += part.Duration;
        }
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("=", "\\=")
        .Replace(";", "\\;").Replace("#", "\\#").Replace("\r", "").Replace("\n", "\\\n");
}
