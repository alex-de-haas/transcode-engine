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
        string? title = null,
        bool hasVideo = true) =>
        new(
            7506.291, pixelFormat, frameRate, language, title,
            profile is { } value
                ? new FfmpegTranscodeEngine.DolbyVisionRecord(value, 6, compatibility, RpuPresent: true, enhancementLayer, BlPresent: true)
                : null,
            hasVideo);

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
        // Should the maps select nothing, ffmpeg would pick streams on its own and re-encode the picture into
        // the discarded file; -vn rules the picture out whatever happens.
        Assert.Contains("-vn", args);
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

    private static FfmpegTranscodeEngine.ProbedStream Stream(int index, string kind) => new(index, kind, "x");

    [Fact]
    public void TracksStage_IsSkippedWhenNothingIsSelected()
    {
        // Explicitly empty selections and no merge: ffmpeg would be given no -map and would select streams on
        // its own, reintroducing the tracks the caller excluded. The stage is not run at all.
        var streams = new[] { Stream(0, "video"), Stream(1, "audio"), Stream(2, "subtitle") };
        Assert.False(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(audio: [], subtitles: []), streams));
    }

    [Fact]
    public void TracksStage_RunsWhenASelectionOrTheInputHasSomethingToCopy()
    {
        var withAudio = new[] { Stream(0, "video"), Stream(1, "audio") };
        var videoOnly = new[] { Stream(0, "video") };
        var withFonts = new[] { Stream(0, "video"), Stream(1, "attachment") };

        // A null selection copies every stream of its kind, so it maps something exactly when the input has one.
        Assert.True(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(), withAudio));
        Assert.False(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(), videoOnly));
        // Subtitles ride with attachments: a null subtitle selection maps the fonts even with no subtitle track.
        Assert.True(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(audio: []), withFonts));
        // An explicit selection answers by its count, whatever the input holds.
        Assert.True(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(audio: [1], subtitles: []), videoOnly));
        // A merge always brings at least one stream — the endpoint requires it.
        var merge = Conversion(audio: [], subtitles: []) with { AdditionalInputs = [new AdditionalInput("/in/dub.mka", [0])] };
        Assert.True(FfmpegTranscodeEngine.TracksStageMapsAnything(merge, videoOnly));
        // An unknown stream list runs the stage: an empty run fails honestly, a skipped one loses tracks.
        Assert.True(FfmpegTranscodeEngine.TracksStageMapsAnything(Conversion(), []));
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
    public void Mkvmerge_WithoutAComposition_TakesTheVideoAlone()
    {
        var args = FfmpegTranscodeEngine.BuildMkvmergeArguments(
            "/out/.movie.part.mkv", "/out/.movie.dv81.hevc", tracks: null, Probe(frameRate: null, language: null));

        Assert.Equal(["--output", "/out/.movie.part.mkv", "/out/.movie.dv81.hevc"], args);
        Assert.DoesNotContain("--no-video", args);
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

    [Theory]
    // mkvmerge on Windows puts a byte-order mark in front of redirected output; read as UTF-8 that is one
    // character ahead of the document, and read in the console code page it was three — both of which
    // JsonDocument refuses. A job on a real profile 7 source failed exactly here, with no log of why.
    [InlineData("\uFEFF{\"tracks\":[{\"id\":1,\"type\":\"video\"}]}")]
    [InlineData("\r\n  {\"tracks\":[{\"id\":1,\"type\":\"video\"}]}\r\n")]
    [InlineData("\r\n\uFEFF  {\"tracks\":[{\"id\":1,\"type\":\"video\"}]}")]
    public void ParseVideoTrackId_SkipsAByteOrderMarkAndWhitespaceAheadOfTheDocument(string identify) =>
        Assert.Equal(1, FfmpegTranscodeEngine.ParseVideoTrackId(identify));

    [Fact]
    public void ParseVideoTrackId_ReadsARealIdentification()
    {
        // mkvmerge v82's own output for an HEVC + AC-3 Matroska, captured from the engine image and kept
        // verbatim: attachments, chapters, container properties, the codec private data — everything the
        // real document carries around the two entries that matter.
        Assert.Equal(0, FfmpegTranscodeEngine.ParseVideoTrackId(RealIdentification));
        Assert.Equal("0:video, 1:audio", FfmpegTranscodeEngine.DescribeTracks(RealIdentification));
    }

    [Theory]
    [InlineData("""{"tracks":[{"id":0,"type":"audio"},{"id":1,"type":"subtitles"}]}""", "0:audio, 1:subtitles")]
    [InlineData("""{"tracks":[]}""", "(empty)")]
    [InlineData("""{"container":{}}""", "(no tracks array)")]
    // Valid JSON of the wrong shape — a root that is not an object, a tracks entry that is not — is
    // described, never thrown on: TryGetProperty on a non-object is an InvalidOperationException, and a
    // diagnostic that crashes has explained nothing.
    [InlineData("[]", "(no tracks array)")]
    [InlineData(""""ok"""", "(no tracks array)")]
    [InlineData("""{"tracks":[1,{"id":0,"type":"video"},"x"]}""", "?:?, 0:video, ?:?")]
    [InlineData("not json", "(unreadable: ")]
    public void DescribeTracks_SaysWhatTheDocumentHeld(string identify, string expected) =>
        Assert.StartsWith(expected, FfmpegTranscodeEngine.DescribeTracks(identify));

    [Theory]
    [InlineData("[]")]
    [InlineData(""""ok"""")]
    [InlineData("""{"tracks":[1,"x"]}""")]
    public void ParseVideoTrackId_TreatsValidJsonOfTheWrongShapeAsNoTrack(string identify) =>
        Assert.Null(FfmpegTranscodeEngine.ParseVideoTrackId(identify));

    private const string RealIdentification = """
        {
          "attachments": [],
          "chapters": [],
          "container": {
            "properties": {
              "container_type": 17,
              "duration": 6484508000000,
              "is_providing_timestamps": true,
              "muxing_application": "Lavf62.12.102",
              "segment_uid": "ae5468f340e13c6cc19b432ea32e8f9a",
              "timestamp_scale": 1000000,
              "writing_application": "Lavf62.12.102"
            },
            "recognized": true,
            "supported": true,
            "type": "Matroska"
          },
          "errors": [],
          "file_name": "/m/Enola Holmes 3 (2026) - HEVC.mkv",
          "global_tags": [
            {
              "num_entries": 3
            }
          ],
          "identification_format_version": 19,
          "track_tags": [
            {
              "num_entries": 2,
              "track_id": 0
            },
            {
              "num_entries": 1,
              "track_id": 1
            }
          ],
          "tracks": [
            {
              "codec": "HEVC/H.265/MPEG-H",
              "id": 0,
              "properties": {
                "chroma_siting": "1,2",
                "codec_id": "V_MPEGH/ISO/HEVC",
                "codec_private_data": "010160000000b000000000003ff000fcfdf8f800000f03200001001840010c01ffff016000000300b0000003000003003f1702402100010029420101016000000300b0000003000003003fa005a201316205ee45914bff2e7f13fac05a810101004022000100074401c072f45364",
                "codec_private_length": 110,
                "color_range": 1,
                "default_duration": 41666700,
                "default_track": false,
                "display_dimensions": "720x304",
                "display_unit": 0,
                "enabled_track": true,
                "forced_track": false,
                "language": "und",
                "minimum_timestamp": 42000000,
                "num_index_entries": 12969,
                "number": 1,
                "packetizer": "mpegh_p2_video",
                "pixel_dimensions": "720x304",
                "tag_duration": "01:48:04.506000000",
                "tag_encoder": "Lavc62.28.102 hevc_videotoolbox",
                "uid": 16707644069576102787
              },
              "type": "video"
            },
            {
              "codec": "AC-3",
              "id": 1,
              "properties": {
                "audio_bits_per_sample": 32,
                "audio_channels": 6,
                "audio_sampling_frequency": 48000,
                "codec_id": "A_AC3",
                "codec_private_length": 0,
                "default_duration": 32000000,
                "default_track": false,
                "enabled_track": true,
                "forced_track": false,
                "language": "und",
                "minimum_timestamp": 0,
                "num_index_entries": 0,
                "number": 2,
                "tag_duration": "01:48:04.508000000",
                "uid": 13934924149706002149
              },
              "type": "audio"
            }
          ],
          "warnings": []
        }
        """;

    [Fact]
    public void Identify_PassesOnlyTheIdentifyOptionsAndTheFile()
    {
        // mkvmerge in identification mode takes the first argument that is not an identify option as the
        // file and refuses a second — a real job failed with "The argument '<path>' is not allowed in
        // identification mode" because --no-bom stood where the file name was expected.
        var args = FfmpegTranscodeEngine.BuildIdentifyArguments("/in/movie.mkv");

        // --output-charset is one of the options MKVToolNix strips before its own parsing, so it is the one
        // general option that is allowed here — and it is what keeps a Cyrillic track name from ending the
        // document mid-string under a locale that is not UTF-8.
        Assert.Equal(["--output-charset", "UTF-8", "--identification-format", "json", "--identify", "/in/movie.mkv"], args);
        Assert.DoesNotContain("--no-bom", args);
    }

    [Fact]
    public void ToolProcess_ReadsBothPipesAsUtf8()
    {
        var psi = FfmpegTranscodeEngine.ToolProcess("mkvmerge");

        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Same(FfmpegTranscodeEngine.ToolOutput, psi.StandardOutputEncoding);
        Assert.Same(FfmpegTranscodeEngine.ToolOutput, psi.StandardErrorEncoding);
        Assert.False(psi.UseShellExecute);
    }

    [Theory]
    // No locale at all — a service started by an init system or by Core in WSL — gets one the platform has.
    [InlineData(null, null, null, true, false, "C.UTF-8")]
    [InlineData(null, null, null, false, true, "en_US.UTF-8")]
    [InlineData(null, null, null, false, false, null)]
    // A locale that is not UTF-8 is overridden: under it mkvmerge stops at the first non-ASCII character.
    [InlineData("C", null, null, true, false, "C.UTF-8")]
    [InlineData(null, null, "POSIX", true, false, "C.UTF-8")]
    // An operator's own UTF-8 locale, under any of the three names and either spelling, is left alone.
    [InlineData(null, null, "en_US.UTF-8", true, false, null)]
    [InlineData(null, "ru_RU.utf8", null, true, false, null)]
    [InlineData("C.UTF-8", null, null, false, true, null)]
    public void LocaleOverride_SetsAUtf8LocaleOnlyWhereNoneIsNamed(
        string? lcAll, string? lcCtype, string? lang, bool isLinux, bool isMacOS, string? expected)
    {
        var environment = new Dictionary<string, string?>();
        if (lcAll is not null) environment["LC_ALL"] = lcAll;
        if (lcCtype is not null) environment["LC_CTYPE"] = lcCtype;
        if (lang is not null) environment["LANG"] = lang;

        Assert.Equal(expected, FfmpegTranscodeEngine.LocaleOverride(environment, isLinux, isMacOS));
    }

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
        // A probe that timed out reports nothing at all. The job runs and the output check decides, which
        // degrades like every other probe here rather than refusing over a timeout.
        Assert.False(default(FfmpegTranscodeEngine.SourceProbe).Probed);
        Assert.Null(FfmpegTranscodeEngine.DolbyVisionConversionError(default));
    }

    [Fact]
    public void ConversionError_RefusesAnInputWithoutAVideoStream()
    {
        // A probe that answered — there is a duration — and found no video is not an unreadable input: it
        // is a file with nothing to convert, refused here rather than three stages later at mkvmerge.
        var audioOnly = new FfmpegTranscodeEngine.SourceProbe(7506.291, null);
        Assert.True(audioOnly.Probed);
        Assert.Contains("no video stream", FfmpegTranscodeEngine.DolbyVisionConversionError(audioOnly));
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
            codec_type=video
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
        Assert.True(probe.HasVideoStream);
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
        var probe = FfmpegTranscodeEngine.ParseSourceProbe("duration=1.0\ncodec_type=video\npix_fmt=yuv420p\nr_frame_rate=0/0\nTAG:language=und\n");

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
