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
        int? defaultSubtitle = null,
        TranscodeVideoCodec codec = TranscodeVideoCodec.Hevc,
        string? sourcePixelFormat = null) =>
        new(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", outputPath, codec, TranscodeHardware.None, null,
                copyVideo, maxHeight, audio, subtitle, defaultAudio, defaultSubtitle),
            durationSeconds: null,
            sourcePixelFormat);

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
    public void BuildArguments_Vaapi_TenBitSource_UploadsP010AndNamesMain10()
    {
        // Regression: the upload format is the VAAPI surface's sw_format, so a hardcoded nv12 converts the
        // frames to 8 bit in software before hwupload ever runs. The loss is silent — the job completes, and
        // only the banding on HDR gradients shows it — so the source depth has to pick the format.
        var args = Engine().BuildArguments(
            JobWith(sourcePixelFormat: "yuv420p10le"), TranscodeHardware.Vaapi);

        Assert.Equal("format=p010,hwupload", ValueAfter(args, "-vf"));
        Assert.Equal("hevc_vaapi", ValueAfter(args, "-c:v"));
        Assert.Equal("main10", ValueAfter(args, "-profile:v"));
    }

    [Fact]
    public void BuildArguments_Vaapi_TenBitSource_KeepsTheGpuScaleInsideTheP010Chain()
    {
        var args = Engine().BuildArguments(
            JobWith(maxHeight: 1080, sourcePixelFormat: "yuv420p10le"), TranscodeHardware.Vaapi);

        Assert.Equal("format=p010,hwupload,scale_vaapi=w=-2:h=1080", ValueAfter(args, "-vf"));
    }

    [Theory]
    [InlineData("yuv420p")]   // the ordinary 8-bit source
    [InlineData(null)]        // ffprobe could not read a pix_fmt
    [InlineData("nv12")]      // digits that are a packing code, not a depth
    public void BuildArguments_Vaapi_EightBitOrUnreadableSource_KeepsNv12(string? pixelFormat)
    {
        var args = Engine().BuildArguments(
            JobWith(sourcePixelFormat: pixelFormat), TranscodeHardware.Vaapi);

        Assert.Equal("format=nv12,hwupload", ValueAfter(args, "-vf"));
        Assert.DoesNotContain("-profile:v", args);
    }

    [Fact]
    public void BuildArguments_Vaapi_TenBitSourceToH264_StaysOnNv12()
    {
        // No shipping VAAPI driver exposes an H.264 High 10 *encode* entrypoint, so p010 here would turn a
        // job that works today (at 8 bit) into a hard "no usable encoding profile" failure. H.264 keeps the
        // depth it always had; a caller who wants the 10 bits asks for HEVC.
        var args = Engine().BuildArguments(
            JobWith(codec: TranscodeVideoCodec.H264, sourcePixelFormat: "yuv420p10le"), TranscodeHardware.Vaapi);

        Assert.Equal("format=nv12,hwupload", ValueAfter(args, "-vf"));
        Assert.Equal("h264_vaapi", ValueAfter(args, "-c:v"));
        Assert.DoesNotContain("-profile:v", args);
    }

    [Fact]
    public void BuildArguments_TenBitSource_LeavesTheNonVaapiPathsAlone()
    {
        // AMF already preserves 10 bit through its own (measured) decode path, and the software encoders
        // follow the decoded format; neither grew a profile argument.
        foreach (var hardware in new[] { TranscodeHardware.Amf, TranscodeHardware.VideoToolbox, TranscodeHardware.None })
        {
            var args = Engine().BuildArguments(JobWith(sourcePixelFormat: "yuv420p10le"), hardware);

            Assert.DoesNotContain("-vf", args);
            Assert.DoesNotContain("-profile:v", args);
        }
    }

    [Fact]
    public void NeedsVaapiTenBit_OnlyForAVaapiReEncodeOfADeepSourceToHevc()
    {
        // The pre-flight capability probe is the expensive half of the fallback, so it must be asked for
        // exactly the case a render-node check cannot answer — and for nothing else.
        var deep = JobWith(sourcePixelFormat: "yuv420p10le");

        Assert.True(FfmpegTranscodeEngine.NeedsVaapiTenBit(deep.Request, TranscodeHardware.Vaapi, "yuv420p10le"));

        // A remux never opens the encoder.
        var copy = JobWith(copyVideo: true, sourcePixelFormat: "yuv420p10le");
        Assert.False(FfmpegTranscodeEngine.NeedsVaapiTenBit(copy.Request, TranscodeHardware.Vaapi, "yuv420p10le"));

        // 8-bit, H.264, and the non-VAAPI families all stay on paths every driver satisfies.
        Assert.False(FfmpegTranscodeEngine.NeedsVaapiTenBit(deep.Request, TranscodeHardware.Vaapi, "yuv420p"));
        var h264 = JobWith(codec: TranscodeVideoCodec.H264, sourcePixelFormat: "yuv420p10le");
        Assert.False(FfmpegTranscodeEngine.NeedsVaapiTenBit(h264.Request, TranscodeHardware.Vaapi, "yuv420p10le"));
        foreach (var other in new[] { TranscodeHardware.None, TranscodeHardware.Amf, TranscodeHardware.VideoToolbox })
        {
            Assert.False(FfmpegTranscodeEngine.NeedsVaapiTenBit(deep.Request, other, "yuv420p10le"));
        }
    }

    [Theory]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv420p10be", 10)]
    [InlineData("yuv422p10le", 10)]
    [InlineData("yuv420p12le", 12)]
    [InlineData("p010le", 10)]
    [InlineData("gbrp10le", 10)]
    [InlineData("yuv420p", 8)]
    [InlineData("yuvj420p", 8)]
    // Formats whose digits encode subsampling or packing rather than a depth must read as 8, so an
    // unmodelled format can only ever keep the pre-existing nv12 path.
    [InlineData("nv12", 8)]
    [InlineData("rgb24", 8)]
    [InlineData("yuyv422", 8)]
    [InlineData("pal8", 8)]
    [InlineData("", 8)]
    [InlineData(null, 8)]
    public void SourceBitDepth_ReadsTheDepthOutOfThePixelFormatName(string? pixelFormat, int expected) =>
        Assert.Equal(expected, FfmpegTranscodeEngine.SourceBitDepth(pixelFormat));

    [Fact]
    public void ParseSourceProbe_ReadsDurationAndPixelFormatByName()
    {
        // The two entries come from different ffprobe sections, so they are matched by key, not line order.
        var probe = FfmpegTranscodeEngine.ParseSourceProbe("pix_fmt=yuv420p10le\nduration=6423.104000\n");

        Assert.Equal(6423.104, probe.DurationSeconds);
        Assert.Equal("yuv420p10le", probe.VideoPixelFormat);
    }

    [Fact]
    public void ParseSourceProbe_AudioOnlyInput_ReportsNoPixelFormat()
    {
        var probe = FfmpegTranscodeEngine.ParseSourceProbe("duration=1.000000\n");

        Assert.Equal(1.0, probe.DurationSeconds);
        Assert.Null(probe.VideoPixelFormat);
    }

    [Fact]
    public void ParseSourceProbe_UnreadableOutput_ReportsNeither()
    {
        var probe = FfmpegTranscodeEngine.ParseSourceProbe("pix_fmt=unknown\nduration=N/A\n");

        Assert.Null(probe.DurationSeconds);
        Assert.Null(probe.VideoPixelFormat);
    }

    [Fact]
    public void BuildArguments_Amf_LeavesTheDecodeOutputFormatUnset()
    {
        // Regression: naming any -hwaccel_output_format breaks one depth or the other. "nv12" pins the
        // decoder-side transfer (which copies but cannot convert), so 10-bit P010 surfaces fail with EINVAL;
        // "d3d11" keeps them on the GPU and needs an hwdownload filter, whose format the graph negotiates
        // before the frames are known, so 10-bit fails with "Invalid output format nv12 for hwframe
        // download". Unset downloads each surface into its own software format — the only variant that
        // handles both depths.
        var args = Engine().BuildArguments(JobWith(), TranscodeHardware.Amf);

        Assert.Equal("d3d11va", ValueAfter(args, "-hwaccel"));
        Assert.DoesNotContain("-hwaccel_output_format", args);
        Assert.DoesNotContain("-vf", args);
        Assert.Equal("hevc_amf", ValueAfter(args, "-c:v"));
    }

    [Fact]
    public void BuildArguments_MaxHeight_Amf_ScalesOnTheCpu()
    {
        var args = Engine().BuildArguments(JobWith(maxHeight: 1080), TranscodeHardware.Amf);

        Assert.Equal("scale=-2:1080", ValueAfter(args, "-vf"));
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
