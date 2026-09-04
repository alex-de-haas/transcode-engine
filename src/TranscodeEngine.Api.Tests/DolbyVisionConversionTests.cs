using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// The Dolby Vision profile 7 → 8.1 conversion, without a process ever starting: the ffmpeg stage that
/// composes everything but the picture, the argument builders for the three tools the picture goes through,
/// the create-time and output checks, the staged progress, and the tool discovery.
/// </summary>
public sealed class DolbyVisionConversionTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJobRequest Conversion(
        IReadOnlyList<int>? audio = null,
        IReadOnlyList<int>? subtitles = null,
        DolbyVisionMode mode = DolbyVisionMode.ToProfile81) =>
        new(
            "/in/movie.mkv", "/out/movie - DV8.mkv", TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
            CopyVideo: true, AudioStreamIndexes: audio, SubtitleStreamIndexes: subtitles, DolbyVision: mode);

    private static FfmpegTranscodeEngine.SourceProbe Probe(
        int? profile = 7,
        int compatibility = 6,
        bool enhancementLayer = true,
        string? pixelFormat = "yuv420p10le",
        string? frameRate = "24000/1001",
        string? language = "eng",
        string? title = null) =>
        new(
            7506.291, pixelFormat, frameRate, language, title,
            profile is { } value
                ? new FfmpegTranscodeEngine.DolbyVisionRecord(value, 6, compatibility, RpuPresent: true, enhancementLayer, BlPresent: true)
                : null);

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

    // ---- the ffmpeg stage: everything but the picture ----

    [Fact]
    public void TracksStage_MapsNoVideoAndNamesNoVideoCodec()
    {
        // The picture takes the tool path; copying fifty gigabytes of it into a file about to be discarded is
        // the one thing this stage must not do.
        const string temp = "/out/.movie - DV8.job-1.tracks.mkv";
        var args = Engine().BuildArguments(new TranscodeJob("job-1", Conversion(), null), TranscodeHardware.None, [temp]);

        var maps = MapTargets(args).ToList();
        Assert.DoesNotContain("0:v:0", maps);
        Assert.DoesNotContain("-c:v", args);
        Assert.Contains("0:a?", maps);
        Assert.Contains("0:s?", maps);
        Assert.Contains("0:t?", maps);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal(temp, args[^1]);
    }

    [Fact]
    public void TracksStage_KeepsTheSelectedTracks()
    {
        var args = Engine().BuildArguments(
            new TranscodeJob("job-1", Conversion(audio: [1, 2], subtitles: [3]), null), TranscodeHardware.None);

        Assert.Equal(["0:1", "0:2", "0:3", "0:t?"], MapTargets(args));
    }

    [Fact]
    public void KeepMode_StillMapsAndCopiesTheVideo()
    {
        // The default must be exactly the job a caller got before the field existed.
        var args = Engine().BuildArguments(
            new TranscodeJob("job-1", Conversion(mode: DolbyVisionMode.Keep), null), TranscodeHardware.None);

        Assert.Contains("0:v:0", MapTargets(args));
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void ConvertsDolbyVision_IsTheProfile81Mode()
    {
        Assert.True(Conversion().ConvertsDolbyVision);
        Assert.False(Conversion(mode: DolbyVisionMode.Keep).ConvertsDolbyVision);
    }

    // ---- the tool stages ----

    [Fact]
    public void Mkvextract_WritesTheTrackAsAnElementaryStream() =>
        Assert.Equal(
            ["/in/movie.mkv", "tracks", "0:/out/.movie.job-1.bl-el.hevc"],
            FfmpegTranscodeEngine.BuildMkvextractArguments("/in/movie.mkv", 0, "/out/.movie.job-1.bl-el.hevc"));

    [Fact]
    public void DoviTool_ConvertsToProfile81AndDiscardsTheEnhancementLayer() =>
        Assert.Equal(
            ["-m", "2", "convert", "--discard", "/out/in.hevc", "-o", "/out/out.hevc"],
            FfmpegTranscodeEngine.BuildDoviToolArguments("/out/in.hevc", "/out/out.hevc"));

    [Fact]
    public void Mkvmerge_RebuildsTimingAndTagsFromTheSource()
    {
        // An elementary stream carries neither timestamps nor tags; the source's frame rate, language and
        // title are put back, and the video goes first.
        var args = FfmpegTranscodeEngine.BuildMkvmergeArguments(
            "/out/.movie.part.mkv", "/out/.movie.dv81.hevc", "/out/.movie.tracks.mkv",
            Probe(frameRate: "24000/1001", language: "eng", title: "Main Feature"));

        Assert.Equal(
            [
                "--output", "/out/.movie.part.mkv",
                "--default-duration", "0:24000/1001fps",
                "--language", "0:eng",
                "--track-name", "0:Main Feature",
                "/out/.movie.dv81.hevc",
                "--no-video", "/out/.movie.tracks.mkv",
            ],
            args);
    }

    [Fact]
    public void Mkvmerge_OmitsWhatTheSourceDidNotState()
    {
        var args = FfmpegTranscodeEngine.BuildMkvmergeArguments(
            "/out/.movie.part.mkv", "/out/.movie.dv81.hevc", "/out/.movie.tracks.mkv",
            Probe(frameRate: null, language: null, title: null));

        Assert.Equal(["--output", "/out/.movie.part.mkv", "/out/.movie.dv81.hevc", "--no-video", "/out/.movie.tracks.mkv"], args);
    }

    [Fact]
    public void ParseVideoTrackId_TakesTheFirstVideoTrack()
    {
        // mkvmerge's ids, not ffprobe's indexes: the two agree on most files and differ on any with attachments.
        const string Identify = """
            {"container":{"type":"Matroska"},"tracks":[
              {"id":0,"type":"audio","codec":"TrueHD Atmos"},
              {"id":1,"type":"video","codec":"HEVC/H.265/MPEG-H"},
              {"id":2,"type":"video","codec":"HEVC/H.265/MPEG-H"}],
             "attachments":[{"id":1,"file_name":"cover.jpg"}]}
            """;
        Assert.Equal(1, FfmpegTranscodeEngine.ParseVideoTrackId(Identify));
    }

    [Theory]
    [InlineData("""{"tracks":[{"id":0,"type":"audio"}]}""")]
    [InlineData("""{"container":{"type":"Matroska"}}""")]
    [InlineData("not json at all")]
    public void ParseVideoTrackId_AnswersNullWithoutAVideoTrack(string identify) =>
        Assert.Null(FfmpegTranscodeEngine.ParseVideoTrackId(identify));

    // ---- what may be converted, and what counts as converted ----

    [Fact]
    public void ConversionError_AcceptsADualLayerProfile7() =>
        Assert.Null(FfmpegTranscodeEngine.DolbyVisionConversionError(Probe(profile: 7)));

    [Theory]
    [InlineData(8, "profile 8")]
    [InlineData(5, "profile 5")]
    public void ConversionError_RefusesEveryOtherProfileByName(int profile, string expected) =>
        Assert.Contains(expected, FfmpegTranscodeEngine.DolbyVisionConversionError(Probe(profile: profile)));

    [Fact]
    public void ConversionError_RefusesAStreamWithoutARecord() =>
        Assert.Contains("no Dolby Vision", FfmpegTranscodeEngine.DolbyVisionConversionError(Probe(profile: null)));

    [Fact]
    public void ConversionError_PassesAnInputTheProbeCouldNotRead()
    {
        // A probe that timed out reports nothing about the video at all. The job runs and the output check
        // decides, which degrades like every other probe here rather than refusing over a timeout.
        Assert.Null(FfmpegTranscodeEngine.DolbyVisionConversionError(Probe(profile: null, pixelFormat: null)));
    }

    [Fact]
    public void OutputError_AcceptsOnlyProfile81()
    {
        Assert.Null(FfmpegTranscodeEngine.DolbyVisionOutputError(Probe(profile: 8, compatibility: 1, enhancementLayer: false)));
        Assert.Contains("profile 7", FfmpegTranscodeEngine.DolbyVisionOutputError(Probe(profile: 7, compatibility: 6)));
        Assert.Contains("compatibility id 4", FfmpegTranscodeEngine.DolbyVisionOutputError(Probe(profile: 8, compatibility: 4)));
        Assert.Contains("no Dolby Vision", FfmpegTranscodeEngine.DolbyVisionOutputError(Probe(profile: null)));
    }

    [Theory]
    [InlineData(null, 100L, false)]
    [InlineData(100L, null, false)]
    [InlineData(199L, 100L, true)]
    [InlineData(200L, 100L, false)]
    public void FreeSpaceError_NeedsTwiceTheInput_AndOnlyWhenBothFiguresAreKnown(long? available, long? input, bool refused) =>
        Assert.Equal(refused, FfmpegTranscodeEngine.FreeSpaceError(available, input) is not null);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 50, 12.5)]
    [InlineData(1, 0, 25)]
    [InlineData(2, 250, 75)]
    [InlineData(3, 100, 100)]
    public void StagePercent_MapsAStageIntoItsQuarter(int stage, double within, double expected) =>
        Assert.Equal(expected, FfmpegTranscodeEngine.StagePercent(stage, FfmpegTranscodeEngine.DolbyVisionStages, within));

    [Fact]
    public void Intermediates_AreHiddenFilesBesideTheOutput()
    {
        var temps = FfmpegTranscodeEngine.DolbyVisionIntermediatePaths("/out/movie - DV8.mkv", "job-1");

        Assert.Equal("/out/.movie - DV8.job-1.tracks.mkv", temps.Tracks);
        Assert.Equal("/out/.movie - DV8.job-1.bl-el.hevc", temps.SourceLayers);
        Assert.Equal("/out/.movie - DV8.job-1.dv81.hevc", temps.ConvertedLayer);
    }

    // ---- the source probe ----

    [Fact]
    public void ParseSourceProbe_ReadsTheRecordTheFrameRateAndTheTags()
    {
        const string Stdout = """
            duration=7506.291000
            pix_fmt=yuv420p10le
            r_frame_rate=24000/1001
            TAG:language=eng
            TAG:title=Main Feature
            dv_profile=7
            dv_level=6
            rpu_present_flag=1
            el_present_flag=1
            bl_present_flag=1
            dv_bl_signal_compatibility_id=6
            """;
        var probe = FfmpegTranscodeEngine.ParseSourceProbe(Stdout);

        Assert.Equal(7506.291, probe.DurationSeconds!.Value, 3);
        Assert.Equal("yuv420p10le", probe.VideoPixelFormat);
        Assert.Equal("24000/1001", probe.VideoFrameRate);
        Assert.Equal("eng", probe.VideoLanguage);
        Assert.Equal("Main Feature", probe.VideoTitle);
        Assert.NotNull(probe.DolbyVision);
        var record = probe.DolbyVision.Value;
        Assert.Equal(7, record.Profile);
        Assert.Equal(6, record.Level);
        Assert.Equal(6, record.CompatibilityId);
        Assert.True(record.RpuPresent);
        Assert.True(record.ElPresent);
        Assert.True(record.BlPresent);
    }

    [Fact]
    public void ParseSourceProbe_WithoutARecordReportsNoDolbyVision()
    {
        // 0/0 is ffprobe's unknown rational and "und" its no-language; neither is a value to put back.
        var probe = FfmpegTranscodeEngine.ParseSourceProbe("duration=1.0\npix_fmt=yuv420p\nr_frame_rate=0/0\nTAG:language=und\n");

        Assert.Null(probe.DolbyVision);
        Assert.Null(probe.VideoFrameRate);
        Assert.Null(probe.VideoLanguage);
        Assert.Null(probe.VideoTitle);
    }

    // ---- staged progress on the job ----

    [Fact]
    public void ReportedProgress_ReplacesTheFfmpegDerivationUntilTheJobCompletes()
    {
        var job = new TranscodeJob("job-1", Conversion(), durationSeconds: 100);
        job.Start(TranscodeHardware.None);

        job.ApplyProgressLine("out_time_us=50000000");
        Assert.Equal(50, job.ToSnapshot().PercentComplete);
        Assert.Equal(50, job.FfmpegPercent);

        job.ReportProgress(12.5);
        job.ApplyProgressLine("out_time_us=90000000");
        job.ApplyProgressLine("speed=2x");
        var staged = job.ToSnapshot();
        Assert.Equal(12.5, staged.PercentComplete);
        // A ratio across tools that read and write at different speeds would be a number nothing stands behind.
        Assert.Null(staged.EtaSeconds);

        job.Complete(JobState.Completed);
        Assert.Equal(100, job.ToSnapshot().PercentComplete);
    }

    [Fact]
    public void ReportedOutputSize_IsWhatTheSnapshotShows()
    {
        var job = new TranscodeJob("job-1", Conversion(), durationSeconds: 100);
        job.ReportOutputSize(12_345);
        Assert.Equal(12_345, job.ToSnapshot().OutputSizeBytes);
    }

    // ---- tool discovery ----

    [Fact]
    public void Locate_FindsABareNameOnThePathAndAnExplicitFile()
    {
        var bin = Directory.CreateTempSubdirectory("te-tools").FullName;
        var doviTool = Path.Combine(bin, "dovi_tool");
        File.WriteAllText(doviTool, "#!/bin/sh\n");

        Assert.Equal(doviTool, DolbyVisionTooling.Locate("dovi_tool", $"/nowhere{Path.PathSeparator}{bin}"));
        Assert.Equal(doviTool, DolbyVisionTooling.Locate(doviTool, pathVariable: null));
        Assert.Null(DolbyVisionTooling.Locate("mkvmerge", bin));
        Assert.Null(DolbyVisionTooling.Locate(Path.Combine(bin, "missing"), bin));
        Assert.Null(DolbyVisionTooling.Locate("", bin));
    }

    [Theory]
    [InlineData("dovi_tool 2.3.3", "2.3.3")]
    [InlineData("mkvmerge v81.0 ('Unmarked') 64-bit", "81.0")]
    [InlineData("nothing numbered here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseVersion_TakesTheFirstDottedNumber(string? banner, string? expected) =>
        Assert.Equal(expected, DolbyVisionTooling.ParseVersion(banner));

    [Fact]
    public void Available_RequiresAllThreeTools()
    {
        var bin = Directory.CreateTempSubdirectory("te-tools").FullName;
        string Tool(string name)
        {
            var path = Path.Combine(bin, name);
            File.WriteAllText(path, "#!/bin/sh\n");
            return path;
        }

        var doviTool = Tool("dovi_tool");
        var mkvmerge = Tool("mkvmerge");
        var complete = new TranscodeEngineSettings
        {
            AppDataDir = bin, MediaRoots = new Dictionary<string, string>(),
            DoviToolPath = doviTool, MkvmergePath = mkvmerge, MkvextractPath = Tool("mkvextract"),
        };
        Assert.True(DolbyVisionTooling.Available(complete));

        var missingExtract = new TranscodeEngineSettings
        {
            AppDataDir = bin, MediaRoots = new Dictionary<string, string>(),
            DoviToolPath = doviTool, MkvmergePath = mkvmerge, MkvextractPath = Path.Combine(bin, "absent"),
        };
        Assert.False(DolbyVisionTooling.Available(missingExtract));
    }
}
