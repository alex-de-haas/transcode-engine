using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;
using Xunit;

namespace TranscodeEngine.Api.Tests;

public sealed class JoinFfmpegFactAttribute : FactAttribute
{
    public JoinFfmpegFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FFMPEG_PATH") is null || Environment.GetEnvironmentVariable("FFPROBE_PATH") is null)
            Skip = "Set FFMPEG_PATH and FFPROBE_PATH to run real video join fixtures.";
    }
}

public sealed class VideoPartJoinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("video-join-").FullName;
    private string Ffmpeg => Environment.GetEnvironmentVariable("FFMPEG_PATH")!;
    private string Ffprobe => Environment.GetEnvironmentVariable("FFPROBE_PATH")!;
    public void Dispose() => Directory.Delete(_root, true);

    private static string ProbeJson(string language = "eng", string codec = "h264", string timeBase = "1/1000") => JsonSerializer.Serialize(new
    {
        format = new { duration = "1.0", start_time = "0" },
        streams = new[] { new { codec_type = "video", codec_name = codec, pix_fmt = "yuv420p", time_base = timeBase, tags = new { language } } },
        chapters = Array.Empty<object>(),
    });

    [Theory]
    [InlineData("rus", "h264", "1/1000", "language")]
    [InlineData("eng", "mpeg4", "1/1000", "codec_name")]
    [InlineData("eng", "h264", "1/90000", "time_base")]
    [InlineData("eng", "vp9", "1/1000", "not supported")]
    public void Preflight_RefusesMismatchedOrUnsupportedTracks(string language, string codec, string timeBase, string reason)
    {
        var first = JoinMediaInfo.Parse("a", ProbeJson());
        var second = JoinMediaInfo.Parse("b", ProbeJson(language, codec, timeBase));
        Assert.Contains(reason, Assert.Throws<ArgumentException>(() => JoinMediaInfo.Validate(first, second)).Message);
    }

    [Fact]
    public void ConcatList_EscapesQuotesAndPreservesOrder_RejectsLineBreaks()
    {
        var info = JoinMediaInfo.Parse("a", ProbeJson());
        Assert.Equal("ffconcat version 1.0\nfile '/a part'\\''s.mkv'\nduration 1\nfile '/b.mkv'\nduration 1\n",
            JoinMediaInfo.ConcatList(["/a part's.mkv", "/b.mkv"], [info, info]));
        Assert.Throws<ArgumentException>(() => JoinMediaInfo.ConcatList(["/a\nfile /etc/passwd", "/b"], [info, info]));
    }

    [Fact]
    public void BuildArguments_MapsTheCombinedTimelineAndChaptersWithoutEncoding()
    {
        using var engine = new FfmpegTranscodeEngine(new TranscodeEngineSettings
        {
            AppDataDir = _root, MediaRoots = new Dictionary<string, string>(),
        }, NullLogger<FfmpegTranscodeEngine>.Instance);
        var request = Request("first.mkv", "second.mkv", "joined.mkv");
        var job = new TranscodeJob(request.ClientJobId!, request, 2);
        Assert.Equal(new[]
        {
            "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", job.JoinListPath,
            "-f", "ffmetadata", "-i", job.JoinMetadataPath, "-map", "0", "-map_metadata", "0",
            "-map_chapters", "1", "-c", "copy", "-progress", "pipe:1", "-nostats", "temporary.mkv",
        }, engine.BuildArguments(job, TranscodeHardware.None, ["temporary.mkv"]));
    }

    [JoinFfmpegFact]
    public async Task Join_CopiesBothPartsInOrder_PreservesSubtitlesChaptersAndOriginals_AndSupportsSeek()
    {
        var first = await Part("part one's.mkv", "red", "First", "1");
        var second = await Part("part two.mkv", "blue", "Second", "1.4");
        var originalA = await File.ReadAllBytesAsync(first); var originalB = await File.ReadAllBytesAsync(second);
        var settings = new TranscodeEngineSettings { AppDataDir = _root, MediaRoots = new Dictionary<string, string>(), FfmpegPath = Ffmpeg, FfprobePath = Ffprobe };
        using var engine = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance);
        await engine.StartAsync(default);
        try
        {
            var output = Path.Combine(_root, "joined.mkv");
            var request = Request(first, second, output);
            var descriptor = await engine.CreateAsync(request, default);
            Assert.Equal(descriptor.JobId, (await engine.CreateAsync(request, default)).JobId);
            await Complete(engine, descriptor.JobId);
            Assert.Equal(originalA, await File.ReadAllBytesAsync(first)); Assert.Equal(originalB, await File.ReadAllBytesAsync(second));
            var joined = JoinMediaInfo.Parse(output, await Run(Ffprobe, "-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", output));
            Assert.InRange(joined.Duration, 2.39, 2.45);
            Assert.Equal(3, joined.Streams.Length);
            Assert.Equal(2, joined.Chapters.Length);
            Assert.InRange(double.Parse(joined.Chapters[1].GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 0.99, 1.02);
            var hashesA = await Frames(first); var hashesB = await Frames(second);
            Assert.Equal(hashesA.Concat(hashesB), await Frames(output));
            Assert.Equal((await Audio(first)).Concat(await Audio(second)), await Audio(output));
            var seek = await Run(Ffmpeg, "-v", "error", "-ss", "1.2", "-i", output, "-map", "0:v:0", "-frames:v", "1", "-f", "framemd5", "-");
            Assert.Contains(hashesB[0], seek);
            var subtitles = await Run(Ffmpeg, "-v", "error", "-i", output, "-map", "0:s:0", "-f", "srt", "-");
            Assert.Contains("Second", subtitles); Assert.Contains("00:00:01,200", subtitles);
            Assert.Empty(Directory.GetFiles(_root, ".*.part.mkv"));
            var reverse = await engine.CreateAsync(Request(second, first, Path.Combine(_root, "reverse.mkv")), default);
            await Complete(engine, reverse.JobId);
            Assert.Equal(hashesB.Concat(hashesA), await Frames(Path.Combine(_root, "reverse.mkv")));
        }
        finally { await engine.StopAsync(default); }
        using var restarted = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance);
        await restarted.StartAsync(default);
        try { Assert.Equal(2, restarted.GetAllSnapshots().Count(snapshot => snapshot.State == "Completed")); }
        finally { await restarted.StopAsync(default); }
    }

    [JoinFfmpegFact]
    public async Task Join_RefusesExistingOutputAndConcurrentWriter_AndCancelsQueuedWork()
    {
        var first = await Part("a.mkv", "red", "A", "1");
        var second = await Part("b.mkv", "blue", "B", "1");
        using var engine = new FfmpegTranscodeEngine(new() { AppDataDir = _root, MediaRoots = new Dictionary<string, string>(), FfmpegPath = Ffmpeg, FfprobePath = Ffprobe }, NullLogger<FfmpegTranscodeEngine>.Instance);
        var output = Path.Combine(_root, "output.mkv");
        await File.WriteAllTextAsync(output, "keep me");
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(Request(first, second, output), default));
        Assert.Equal("keep me", await File.ReadAllTextAsync(output));
        File.Delete(output);
        var descriptor = await engine.CreateAsync(Request(first, second, output), default);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(Request(first, second, output), default));
        await engine.CancelAsync(descriptor.JobId, default);
        Assert.Equal("Cancelled", engine.GetSnapshot(descriptor.JobId)!.State);
        Assert.False(File.Exists(output));
    }

    [JoinFfmpegFact]
    public async Task Restart_RecordsInterruptedJoinAndCleansItsTemporaryFiles()
    {
        var a = await Part("a.mkv", "red", "A", "1");
        var b = await Part("b.mkv", "blue", "B", "1");
        var settings = new TranscodeEngineSettings { AppDataDir = _root, MediaRoots = new Dictionary<string, string>(), FfmpegPath = Ffmpeg, FfprobePath = Ffprobe };
        using var original = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance);
        var descriptor = await original.CreateAsync(Request(a, b, Path.Combine(_root, "output.mkv")), default);
        var temporary = Path.Combine(_root, $".output.{descriptor.JobId}.part.mkv");
        await File.WriteAllTextAsync(temporary, "interrupted output");
        using var restarted = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance);
        await restarted.StartAsync(default);
        try
        {
            var status = restarted.GetSnapshot(descriptor.JobId)!;
            Assert.Equal("Failed", status.State);
            Assert.Contains("restarted", status.Error);
            Assert.False(File.Exists(temporary));
            Assert.False(File.Exists(Path.Combine(_root, "output.mkv")));
            Assert.True(File.Exists(a)); Assert.True(File.Exists(b));
        }
        finally { await restarted.StopAsync(default); }
    }

    [JoinFfmpegFact]
    public async Task Join_NormalizesNonzeroMatroskaStart_AndPreservesFontAttachments()
    {
        var a = await Part("a.mkv", "red", "A", "1");
        var b = await Part("b.mkv", "blue", "B", "1.4");
        var font = Path.Combine(_root, "fixture.ttf");
        await File.WriteAllBytesAsync(font, [0, 1, 0, 0, 10, 20, 30, 40]);
        var first = Path.Combine(_root, "a-font.mkv");
        var second = Path.Combine(_root, "b-offset-font.mkv");
        await Run(Ffmpeg, "-v", "error", "-i", a, "-map", "0", "-c", "copy", "-attach", font,
            "-metadata:s:t", "mimetype=application/x-truetype-font", first);
        await Run(Ffmpeg, "-v", "error", "-i", b, "-map", "0", "-c", "copy", "-map_chapters", "-1", "-output_ts_offset", "5",
            "-attach", font, "-metadata:s:t", "mimetype=application/x-truetype-font", second);
        using var engine = new FfmpegTranscodeEngine(new() { AppDataDir = _root, MediaRoots = new Dictionary<string, string>(),
            FfmpegPath = Ffmpeg, FfprobePath = Ffprobe }, NullLogger<FfmpegTranscodeEngine>.Instance);
        await engine.StartAsync(default);
        try
        {
            var output = Path.Combine(_root, "joined.mkv");
            var descriptor = await engine.CreateAsync(Request(first, second, output), default);
            await Complete(engine, descriptor.JobId);
            Assert.Equal((await Frames(first)).Concat(await Frames(second)), await Frames(output));
            Assert.Equal((await Audio(first)).Concat(await Audio(second)), await Audio(output));
            var probe = await Run(Ffprobe, "-v", "error", "-show_streams", "-show_data_hash", "sha256", "-of", "json", output);
            using var json = JsonDocument.Parse(probe);
            var attachment = Assert.Single(json.RootElement.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_type").GetString() == "attachment");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(font))).ToLowerInvariant();
            Assert.Equal("SHA256:" + hash, attachment.GetProperty("extradata_hash").GetString());
            var subtitles = await Run(Ffmpeg, "-v", "error", "-i", output, "-map", "0:s:0", "-f", "srt", "-");
            Assert.Contains("00:00:01,200", subtitles);
        }
        finally { await engine.StopAsync(default); }
    }

    private static TranscodeJobRequest Request(string a, string b, string output) => new(a, output,
        TranscodeVideoCodec.H264, TranscodeHardware.None, null, CopyVideo: true,
        JoinPaths: [a, b], ClientJobId: Guid.NewGuid().ToString("n"));

    private async Task<string> Part(string filename, string color, string caption, string duration)
    {
        var subtitle = Path.Combine(_root, caption + ".srt");
        await File.WriteAllTextAsync(subtitle, $"1\n00:00:00,200 --> 00:00:00,700\n{caption}\n");
        var metadata = Path.Combine(_root, caption + ".txt");
        await File.WriteAllTextAsync(metadata, $";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=900\ntitle={caption}\n");
        var path = Path.Combine(_root, filename);
        await Run(Ffmpeg, "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=160x90:r=25:d={duration}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={duration}", "-i", subtitle,
            "-f", "ffmetadata", "-i", metadata, "-map", "0", "-map", "1", "-map", "2", "-map_chapters", "3",
            "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:s:0", "language=eng", "-t", duration, path);
        return path;
    }

    private async Task<byte[]> Audio(string path)
    {
        var destination = Path.Combine(_root, Guid.NewGuid() + ".pcm");
        await Run(Ffmpeg, "-v", "error", "-i", path, "-map", "0:a:0", "-f", "s16le", destination);
        return await File.ReadAllBytesAsync(destination);
    }

    private async Task<string[]> Frames(string path) => (await Run(Ffmpeg, "-v", "error", "-i", path,
        "-map", "0:v:0", "-f", "framemd5", "-")).Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(line => line.Split(',')[^1].Trim()).ToArray();

    private static async Task Complete(FfmpegTranscodeEngine engine, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (engine.GetSnapshot(id)?.State is "Queued" or "Running") await Task.Delay(20, timeout.Token);
        var snapshot = engine.GetSnapshot(id)!;
        Assert.True(snapshot.State == "Completed", $"{snapshot.State}: {snapshot.Error}");
    }

    private static async Task<string> Run(string binary, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var psi = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
