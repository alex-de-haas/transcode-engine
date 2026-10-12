using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class VideoSelectionFfmpegFactAttribute : FactAttribute
{
    public VideoSelectionFfmpegFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FFMPEG_PATH") is null || Environment.GetEnvironmentVariable("FFPROBE_PATH") is null)
            Skip = "Set FFMPEG_PATH and FFPROBE_PATH to verify real selected-track output.";
    }
}

public sealed class VideoSelectionIntegrationTests
{
    [VideoSelectionFfmpegFact]
    public async Task Selected_video_audio_subtitles_sidecar_and_names_survive_copy_and_encode()
    {
        var root = Directory.CreateTempSubdirectory("selected-tracks-").FullName;
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH")!;
        var ffprobe = Environment.GetEnvironmentVariable("FFPROBE_PATH")!;
        try
        {
            var input = Path.Combine(root, "source.mkv");
            var sidecar = Path.Combine(root, "dub.mka");
            var subtitle = Path.Combine(root, "captions.srt");
            await File.WriteAllTextAsync(subtitle, "1\n00:00:00,100 --> 00:00:00,800\nSelected subtitle\n");
            await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=25:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-f", "lavfi", "-i", "color=c=blue:s=96x64:r=25:d=1", "-f", "lavfi", "-i", "sine=frequency=880:duration=1", "-i", subtitle,
                "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-map", "4", "-c:v", "libx264", "-threads", "1", "-c:a", "pcm_s16le", "-c:s", "srt",
                "-metadata:s:v:0", "title=Wrong picture", "-metadata:s:v:1", "title=Selected original", "-metadata:s:a:1", "language=eng", "-metadata:s:s:0", "language=rus",
                "-disposition:v:0", "default", "-disposition:v:1", "0", "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "forced", input);
            await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=220:duration=1", "-c:a", "pcm_s16le", "-disposition:a:0", "0", sidecar);
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
            using var engine = new FfmpegTranscodeEngine(new() { AppDataDir = root, MediaRoots = new Dictionary<string, string>(), FfmpegPath = ffmpeg, FfprobePath = ffprobe }, NullLogger<FfmpegTranscodeEngine>.Instance);
            await engine.StartAsync(default);
            try
            {
                foreach (var copy in new[] { true, false })
                {
                    var output = Path.Combine(root, copy ? "remux.mkv" : "convert.mkv");
                    var request = new TranscodeJobRequest(input, output, TranscodeVideoCodec.H264, TranscodeHardware.None, null,
                        CopyVideo: copy, VideoStreamIndex: 2, AudioStreamIndexes: [3], SubtitleStreamIndexes: [4],
                        AdditionalInputs: [new(sidecar, [0])], MetadataOverrides: [new(0, 2, Title: "Chosen picture"), new(1, 0, Title: "External dub")]);
                    var job = await engine.CreateAsync(request, default);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (engine.GetSnapshot(job.JobId)?.State is "Queued" or "Running") await Task.Delay(20, timeout.Token);
                    var snapshot = engine.GetSnapshot(job.JobId)!;
                    Assert.True(snapshot.State == "Completed", snapshot.Error);
                    using var json = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-of", "json", output));
                    var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                    Assert.Equal(4, streams.Length);
                    Assert.Equal(96, streams[0].GetProperty("width").GetInt32());
                    Assert.Equal("Chosen picture", streams[0].GetProperty("tags").GetProperty("title").GetString());
                    Assert.Equal("eng", streams[1].GetProperty("tags").GetProperty("language").GetString());
                    Assert.Equal("External dub", streams[2].GetProperty("tags").GetProperty("title").GetString());
                    Assert.Equal("subtitle", streams[3].GetProperty("codec_type").GetString());
                    Assert.Equal(1, streams[3].GetProperty("disposition").GetProperty("forced").GetInt32());
                    Assert.Equal(0, streams[1].GetProperty("disposition").GetProperty("default").GetInt32());
                    Assert.Equal(0, streams[0].GetProperty("disposition").GetProperty("default").GetInt32());
                }
                await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(new(input, Path.Combine(root, "invalid.mkv"), TranscodeVideoCodec.H264,
                    TranscodeHardware.None, null, VideoStreamIndex: 1), default));
            }
            finally { await engine.StopAsync(default); }
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> Run(string binary, params string[] args)
    {
        var psi = new ProcessStartInfo(binary) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
