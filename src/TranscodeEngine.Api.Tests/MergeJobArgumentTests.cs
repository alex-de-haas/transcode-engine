using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Argument construction for a merge — a job that names further files whose streams join the output — and
/// for the per-stream metadata overrides, which apply to any job. The positions these produce are what
/// ffmpeg writes metadata and dispositions against, so they are asserted as positions, not as flags.
/// </summary>
public sealed class MergeJobArgumentTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJob Merge(
        IReadOnlyList<AdditionalInput> inputs,
        IReadOnlyList<int>? primaryAudio = null,
        IReadOnlyList<int>? primarySubtitles = null,
        IReadOnlyList<StreamMetadataOverride>? overrides = null,
        int? defaultAudio = null,
        string output = "/out/movie.mkv") =>
        new(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", output, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                CopyVideo: true, MaxHeight: null, AudioStreamIndexes: primaryAudio,
                SubtitleStreamIndexes: primarySubtitles, DefaultAudioStreamIndex: defaultAudio,
                DefaultSubtitleStreamIndex: null, AdditionalInputs: inputs, MetadataOverrides: overrides),
            durationSeconds: null);

    private static List<string> Inputs(List<string> args)
    {
        var inputs = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-i")
            {
                inputs.Add(args[i + 1]);
            }
        }

        return inputs;
    }

    private static List<string> MapTargets(List<string> args)
    {
        var maps = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                maps.Add(args[i + 1]);
            }
        }

        return maps;
    }

    private static string? ValueAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    [Fact]
    public void Every_additional_input_becomes_an_ffmpeg_input_after_the_primary()
    {
        var args = Engine().BuildArguments(
            Merge([new AdditionalInput("/in/dub.mka", [0]), new AdditionalInput("/in/subs.ass", null, [0])], primaryAudio: [1]),
            TranscodeHardware.None);

        Assert.Equal(["/in/movie.mkv", "/in/dub.mka", "/in/subs.ass"], Inputs(args));
    }

    [Fact]
    public void The_primary_video_and_every_selected_stream_is_mapped_in_order()
    {
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/dub1.mka", [0]), new AdditionalInput("/in/dub2.mka", [0])],
                primaryAudio: [1, 2],
                primarySubtitles: [3]),
            TranscodeHardware.None);

        // Audio first (primary's own, then each appended file's), then subtitles, then the attachment fonts.
        Assert.Equal(["0:v:0", "0:1", "0:2", "1:0", "2:0", "0:3", "0:t?"], MapTargets(args));
    }

    [Fact]
    public void A_merge_stream_copies_every_track()
    {
        var args = Engine().BuildArguments(Merge([new AdditionalInput("/in/dub.mka", [0])], primaryAudio: [1]), TranscodeHardware.None);

        Assert.Equal("copy", ValueAfter(args, "-c:v"));
        Assert.Equal("copy", ValueAfter(args, "-c:a"));
        Assert.Equal("copy", ValueAfter(args, "-c:s"));
    }

    [Fact]
    public void An_appended_tracks_metadata_lands_on_its_own_output_position()
    {
        // The primary contributes two audio tracks, so the appended dub is audio position 2.
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/dub.mka", [0])],
                primaryAudio: [1, 2],
                overrides: [new StreamMetadataOverride(1, 0, "rus", "MVO заКАДРЫ")]),
            TranscodeHardware.None);

        Assert.Equal("language=rus", ValueAfter(args, "-metadata:s:a:2"));
        var titleIndex = args.LastIndexOf("-metadata:s:a:2");
        Assert.Equal("title=MVO заКАДРЫ", args[titleIndex + 1]);
    }

    [Fact]
    public void A_stream_of_the_primary_input_can_be_relabelled_too()
    {
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/dub.mka", [0])],
                primaryAudio: [1, 2],
                overrides: [new StreamMetadataOverride(0, 2, Title: "Original")]),
            TranscodeHardware.None);

        // Absolute index 2 is the primary's second selected track, so audio position 1.
        Assert.Equal("title=Original", ValueAfter(args, "-metadata:s:a:1"));
        Assert.DoesNotContain("-metadata:s:a:0", args);
    }

    [Fact]
    public void A_field_left_null_is_not_written_so_the_sources_own_tag_survives()
    {
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/dub.mka", [0])],
                primaryAudio: [1],
                overrides: [new StreamMetadataOverride(1, 0, Language: "rus")]),
            TranscodeHardware.None);

        Assert.Equal("language=rus", ValueAfter(args, "-metadata:s:a:1"));
        Assert.DoesNotContain(args, argument => argument.StartsWith("title=", StringComparison.Ordinal));
    }

    [Fact]
    public void A_subtitle_override_addresses_the_subtitle_positions()
    {
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/subs.ass", null, [0])],
                primaryAudio: [1],
                primarySubtitles: [2],
                overrides: [new StreamMetadataOverride(1, 0, "rus", "Forced")]),
            TranscodeHardware.None);

        Assert.Equal("language=rus", ValueAfter(args, "-metadata:s:s:1"));
    }

    [Fact]
    public void A_default_track_still_addresses_positions_across_every_input()
    {
        var args = Engine().BuildArguments(
            Merge([new AdditionalInput("/in/dub.mka", [0])], primaryAudio: [1, 2], defaultAudio: 2),
            TranscodeHardware.None);

        // Positions 0..2: the primary's two tracks then the appended one; only position 1 is the default.
        Assert.Equal("0", ValueAfter(args, "-disposition:a:0"));
        Assert.Equal("default", ValueAfter(args, "-disposition:a:1"));
        Assert.Equal("0", ValueAfter(args, "-disposition:a:2"));
    }

    [Fact]
    public void Overrides_apply_to_a_plain_transcode_as_well()
    {
        var job = new TranscodeJob(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", "/out/movie.mkv", TranscodeVideoCodec.Hevc, TranscodeHardware.None, 22,
                CopyVideo: false, MaxHeight: null, AudioStreamIndexes: [1, 2],
                MetadataOverrides: [new StreamMetadataOverride(0, 1, Title: "Fixed")]),
            durationSeconds: null);

        var args = Engine().BuildArguments(job, TranscodeHardware.None);

        Assert.Equal("title=Fixed", ValueAfter(args, "-metadata:s:a:0"));
        Assert.Single(Inputs(args));
    }

    [Fact]
    public void Subtitles_are_dropped_for_a_non_matroska_output_and_so_are_their_overrides()
    {
        var args = Engine().BuildArguments(
            Merge(
                [new AdditionalInput("/in/dub.mka", [0])],
                primaryAudio: [1],
                primarySubtitles: [2],
                overrides: [new StreamMetadataOverride(0, 2, Title: "Ignored")],
                output: "/out/movie.mp4"),
            TranscodeHardware.None);

        Assert.DoesNotContain("0:2", MapTargets(args));
        Assert.DoesNotContain(args, argument => argument.StartsWith("-metadata:s:s:", StringComparison.Ordinal));
    }
}
