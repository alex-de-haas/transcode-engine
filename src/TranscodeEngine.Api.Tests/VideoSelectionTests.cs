using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class VideoSelectionTests
{
    private static TranscodeJobRequest Request(bool copy = true) => new("/in/movie.mkv", "/out/movie.mkv", TranscodeVideoCodec.H264,
        TranscodeHardware.None, null, CopyVideo: copy, AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: 3,
        MetadataOverrides: [new(0, 3, Title: "Selected picture")]);
    private static FfmpegTranscodeEngine Engine() => new(new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() }, NullLogger<FfmpegTranscodeEngine>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_picture_and_its_name_are_used_for_copy_and_encode(bool copy)
    {
        var args = Engine().BuildArguments(new TranscodeJob("selected", Request(copy), null), TranscodeHardware.None);
        Assert.Contains("0:3", args);
        Assert.DoesNotContain("0:v:0", args);
        Assert.Equal("title=Selected picture", args[args.IndexOf("-metadata:s:v:0") + 1]);
        Assert.Equal(copy ? "copy" : "libx264", args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void Empty_title_removes_the_tag()
    {
        var args = Engine().BuildArguments(new TranscodeJob("selected", Request() with { MetadataOverrides = [new(0, 3, Title: "")] }, null), TranscodeHardware.None);
        Assert.Contains("title=", args);
    }

    [Fact]
    public void Missing_wrong_type_and_failed_probe_are_refused()
    {
        Assert.NotNull(FfmpegTranscodeEngine.VideoSelectionError(Request(), []));
        Assert.NotNull(FfmpegTranscodeEngine.VideoSelectionError(Request(), [new(3, "audio", "aac")]));
        Assert.NotNull(FfmpegTranscodeEngine.VideoSelectionError(Request(), [new(3, "video", "h264"), new(1, "subtitle", "subrip")]));
        Assert.Null(FfmpegTranscodeEngine.VideoSelectionError(Request(), [new(3, "video", "h264"), new(1, "audio", "aac")]));
    }

    [Fact]
    public void Cover_attachments_do_not_shift_the_Matroska_video_identity()
    {
        var streams = FfmpegTranscodeEngine.ParseProbedStreams("""{"streams":[{"index":0,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},{"index":2,"codec_type":"video","codec_name":"hevc"},{"index":4,"codec_type":"video","codec_name":"hevc"}]}""");
        Assert.Equal((1, 2), FfmpegTranscodeEngine.VideoTrackPosition(streams, 4));
        Assert.Equal((-1, 2), FfmpegTranscodeEngine.VideoTrackPosition(streams, 0));
        var source = FfmpegTranscodeEngine.ParseSourceProbe("codec_type=video\nDISPOSITION:default=0\nDISPOSITION:forced=1\nTAG:title=Selected");
        var args = FfmpegTranscodeEngine.BuildMkvmergeArguments("out.mkv", "v.hevc", null, source);
        Assert.Equal("0:0", args[args.IndexOf("--default-track-flag") + 1]);
        Assert.Equal("0:1", args[args.IndexOf("--forced-display-flag") + 1]);
    }

    [Fact]
    public void Dolby_vision_extraction_selects_the_corresponding_video_not_the_first()
    {
        const string json = """{"tracks":[{"type":"video","id":0},{"type":"audio","id":1},{"type":"video","id":7}]}""";
        Assert.Equal(7, FfmpegTranscodeEngine.ParseVideoTrackId(json, 1, 2));
        Assert.Null(FfmpegTranscodeEngine.ParseVideoTrackId(json, 1, 3));
        Assert.Null(FfmpegTranscodeEngine.ParseVideoTrackId(json, 2, 2));
    }

    [Fact]
    public void Dolby_vision_final_mux_uses_selected_video_metadata_and_override()
    {
        var source = new FfmpegTranscodeEngine.SourceProbe(1, "yuv420p10le", "24/1", "eng", "Old");
        var args = FfmpegTranscodeEngine.BuildMkvmergeArguments("out.mkv", "video.hevc", "tracks.mkv", source, new(0, 3, Title: "New picture"));
        Assert.Equal("0:New picture", args[args.IndexOf("--track-name") + 1]);
        Assert.Equal("0:eng", args[args.IndexOf("--language") + 1]);
        var cleared = FfmpegTranscodeEngine.BuildMkvmergeArguments("out.mkv", "video.hevc", null, source, new(0, 3, Title: ""));
        Assert.DoesNotContain("--track-name", cleared);
    }

    [Fact]
    public void Dolby_vision_tracks_stage_never_maps_the_selected_video()
    {
        var args = Engine().BuildArguments(new TranscodeJob("dv", Request() with { DolbyVision = DolbyVisionMode.ToProfile81 }, null), TranscodeHardware.None);
        Assert.DoesNotContain("0:3", args);
        Assert.DoesNotContain("-metadata:s:v:0", args);
        Assert.Contains("0:1", args);
    }
}
