using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class FfmpegTranscodeEngineTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJob Job(string outputPath) =>
        new(
            "job-1",
            new TranscodeJobRequest("/in/movie.mkv", outputPath, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null),
            durationSeconds: null);

    private static TranscodeJob JobWith(
        string outputPath = "/out/movie - HEVC.mkv",
        bool copyVideo = false,
        int? maxHeight = null,
        IReadOnlyList<int>? audio = null,
        IReadOnlyList<int>? subtitle = null,
        int? defaultAudio = null,
        int? defaultSubtitle = null) =>
        new(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", outputPath, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                copyVideo, maxHeight, audio, subtitle, defaultAudio, defaultSubtitle),
            durationSeconds: null);

    private static IEnumerable<string> MapTargets(List<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                yield return args[i + 1];
            }
        }
    }

    private static string? ValueAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    [Fact]
    public void BuildArguments_WritesToDestinationPath_WhenProvided()
    {
        // The engine encodes to a temp path and renames it onto the real output only on success, so
        // BuildArguments must target that destination — while the muxer/subtitle decision still keys off
        // the real (.mkv) output.
        const string temp = "/out/.movie - HEVC.job-1.part.mkv";
        var args = Engine().BuildArguments(Job("/out/movie - HEVC.mkv"), TranscodeHardware.None, temp);

        Assert.Equal(temp, args[^1]);
        Assert.Contains("0:s?", MapTargets(args));
    }

    [Fact]
    public void BuildArguments_DefaultsDestinationToRequestOutput()
    {
        var args = Engine().BuildArguments(Job("/out/movie - HEVC.mkv"), TranscodeHardware.None);

        Assert.Equal("/out/movie - HEVC.mkv", args[^1]);
    }

    [Fact]
    public void BuildArguments_MapsPrimaryVideoAndAllAudio()
    {
        var args = Engine().BuildArguments(Job("/out/movie - HEVC.mkv"), TranscodeHardware.None);

        var maps = MapTargets(args).ToList();
        Assert.Contains("0:v:0", maps);
        Assert.Contains("0:a?", maps);
        // Audio is copied, never re-encoded.
        Assert.Equal("copy", ValueAfter(args, "-c:a"));
    }

    [Fact]
    public void BuildArguments_Matroska_KeepsAllSubtitlesAndAttachments()
    {
        var args = Engine().BuildArguments(Job("/out/movie - HEVC.mkv"), TranscodeHardware.None);

        var maps = MapTargets(args).ToList();
        Assert.Contains("0:s?", maps);
        Assert.Contains("0:t?", maps);
        Assert.Equal("copy", ValueAfter(args, "-c:s"));
    }

    [Fact]
    public void BuildArguments_NonMatroska_KeepsAudioButOmitsSubtitleCopy()
    {
        var args = Engine().BuildArguments(Job("/out/movie - HEVC.mp4"), TranscodeHardware.None);

        var maps = MapTargets(args).ToList();
        Assert.Contains("0:a?", maps);
        // mp4 can't reliably stream-copy arbitrary subtitle codecs, so they're left out (rather than
        // failing the whole job).
        Assert.DoesNotContain("0:s?", maps);
        Assert.DoesNotContain("0:t?", maps);
        Assert.DoesNotContain("-c:s", args);
    }

    [Fact]
    public void BuildArguments_SelectedAudio_MapsOnlyThoseIndexes()
    {
        var args = Engine().BuildArguments(JobWith(audio: new[] { 1, 4 }), TranscodeHardware.None);

        var maps = MapTargets(args).ToList();
        Assert.Contains("0:1", maps);
        Assert.Contains("0:4", maps);
        Assert.DoesNotContain("0:a?", maps);
    }

    [Fact]
    public void BuildArguments_EmptySubtitleSelection_DropsSubtitlesAndAttachments()
    {
        var args = Engine().BuildArguments(JobWith(subtitle: Array.Empty<int>()), TranscodeHardware.None);

        var maps = MapTargets(args).ToList();
        Assert.DoesNotContain("0:s?", maps);
        Assert.DoesNotContain("0:t?", maps);
        // Audio wasn't restricted, so it's still copied in full.
        Assert.Contains("0:a?", maps);
    }

    [Fact]
    public void BuildArguments_CopyVideo_RemuxesWithoutEncoderScaleOrHwaccel()
    {
        var args = Engine().BuildArguments(JobWith(copyVideo: true, maxHeight: 1080), TranscodeHardware.Vaapi);

        Assert.Equal("copy", ValueAfter(args, "-c:v"));
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-hwaccel", args);
    }

    [Fact]
    public void BuildArguments_MaxHeight_Software_AddsCpuScale()
    {
        var args = Engine().BuildArguments(JobWith(maxHeight: 1080), TranscodeHardware.None);

        Assert.Equal("scale=-2:1080", ValueAfter(args, "-vf"));
    }

    [Fact]
    public void BuildArguments_MaxHeight_Vaapi_ScalesOnGpu()
    {
        var args = Engine().BuildArguments(JobWith(maxHeight: 1080), TranscodeHardware.Vaapi);

        Assert.Equal("format=nv12,hwupload,scale_vaapi=w=-2:h=1080", ValueAfter(args, "-vf"));
    }

    [Fact]
    public void BuildArguments_Amf_DownloadsSurfacesWithoutPinningTheDecodeFormat()
    {
        // Regression: pinning -hwaccel_output_format to nv12 made the decoder-side transfer fail with EINVAL
        // on every 10-bit source (its surfaces are P010, and that transfer cannot convert). The decode must
        // stay on d3d11 and the download must accept both depths.
        var args = Engine().BuildArguments(JobWith(), TranscodeHardware.Amf);

        Assert.Equal("d3d11", ValueAfter(args, "-hwaccel_output_format"));
        Assert.Equal("hwdownload,format=nv12|p010", ValueAfter(args, "-vf"));
        Assert.Equal("hevc_amf", ValueAfter(args, "-c:v"));
    }

    [Fact]
    public void BuildArguments_MaxHeight_Amf_ScalesAfterTheDownload()
    {
        var args = Engine().BuildArguments(JobWith(maxHeight: 1080), TranscodeHardware.Amf);

        Assert.Equal("hwdownload,format=nv12|p010,scale=-2:1080", ValueAfter(args, "-vf"));
    }

    [Fact]
    public void BuildArguments_DefaultAudio_SetsDispositionByOutputPosition()
    {
        var args = Engine().BuildArguments(JobWith(audio: new[] { 1, 4, 6 }, defaultAudio: 4), TranscodeHardware.None);

        // Absolute index 4 is the 2nd mapped audio (position 1) → it becomes default, the rest are cleared.
        Assert.Equal("default", ValueAfter(args, "-disposition:a:1"));
        Assert.Equal("0", ValueAfter(args, "-disposition:a:0"));
        Assert.Equal("0", ValueAfter(args, "-disposition:a:2"));
    }

    [Fact]
    public void BuildArguments_DefaultSubtitle_SetsDispositionByOutputPosition()
    {
        var args = Engine().BuildArguments(JobWith(subtitle: new[] { 2, 5 }, defaultSubtitle: 5), TranscodeHardware.None);

        Assert.Equal("default", ValueAfter(args, "-disposition:s:1"));
        Assert.Equal("0", ValueAfter(args, "-disposition:s:0"));
    }

    [Fact]
    public void BuildArguments_DefaultNotInSelection_LeavesDispositionsUntouched()
    {
        // Defence in depth: a default index that isn't one of the mapped tracks must not clear every default.
        var args = Engine().BuildArguments(JobWith(audio: new[] { 1, 4 }, defaultAudio: 9), TranscodeHardware.None);

        Assert.DoesNotContain(args, arg => arg.StartsWith("-disposition:", StringComparison.Ordinal));
    }

    // ---- Interrupted-wait classification (cancel vs shutdown vs watchdog) ----

    [Theory]
    [InlineData(true, false, JobState.Cancelled)]  // user cancel
    [InlineData(false, true, JobState.Cancelled)]  // engine shutdown
    [InlineData(true, true, JobState.Cancelled)]   // cancel racing shutdown
    [InlineData(false, false, JobState.Failed)]    // genuine no-progress watchdog trip
    public void ClassifyInterruptedWait_CancelOrShutdownIsCancelled_OnlyBareWaitIsWatchdogFailure(
        bool cancelRequested, bool shutdownRequested, JobState expected)
    {
        // A user cancel must report Cancelled even when the watchdog token is what actually fired (a
        // killed-but-slow-to-exit ffmpeg still emits no progress).
        Assert.Equal(expected, FfmpegTranscodeEngine.ClassifyInterruptedWait(cancelRequested, shutdownRequested));
    }

    // ---- Terminal-job retention policy ----

    [Fact]
    public void SelectTerminalJobsToEvict_EvictsJobsOlderThanRetention()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(10);
        var retention = TimeSpan.FromHours(1);
        (string, DateTimeOffset?)[] jobs =
        [
            ("old", now - TimeSpan.FromHours(2)),
            ("fresh", now - TimeSpan.FromMinutes(30)),
            ("edge", now - retention), // exactly at retention → not strictly older → kept
        ];

        var evicted = FfmpegTranscodeEngine.SelectTerminalJobsToEvict(jobs, now, retention, maxRetained: 100);

        Assert.Equal(["old"], evicted);
    }

    [Fact]
    public void SelectTerminalJobsToEvict_CapsRetainedCount_DroppingOldestFirst()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(10);
        (string, DateTimeOffset?)[] jobs =
        [
            ("a", now - TimeSpan.FromMinutes(4)),
            ("b", now - TimeSpan.FromMinutes(3)),
            ("c", now - TimeSpan.FromMinutes(2)),
            ("d", now - TimeSpan.FromMinutes(1)),
        ];

        // All within retention, but the cap is 2 → the two oldest are evicted, newest kept.
        var evicted = FfmpegTranscodeEngine.SelectTerminalJobsToEvict(jobs, now, TimeSpan.FromHours(1), maxRetained: 2);

        Assert.Equal(["a", "b"], evicted);
    }

    [Fact]
    public void SelectTerminalJobsToEvict_KeepsEverythingWithinRetentionAndCap()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(10);
        (string, DateTimeOffset?)[] jobs =
        [
            ("a", now - TimeSpan.FromMinutes(2)),
            ("b", now),
        ];

        Assert.Empty(FfmpegTranscodeEngine.SelectTerminalJobsToEvict(jobs, now, TimeSpan.FromHours(1), maxRetained: 10));
    }
}
