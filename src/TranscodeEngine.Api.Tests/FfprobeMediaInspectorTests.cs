using TranscodeEngine.Api.Probing;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Exercises the ffprobe→<see cref="ProbeResponse"/> translation against JSON shaped exactly as ffprobe
/// emits it, so no process ever starts. The fixtures are trimmed from real output of files in the
/// consumer's development library.
/// </summary>
public sealed class FfprobeMediaInspectorTests
{
    private static string Json(string streams, string format = """{"format_name":"matroska,webm","duration":"7506.291000","bit_rate":"7699019","size":"7223885097"}""") =>
        $$"""{"streams":[{{streams}}],"format":{{format}}}""";

    private const string Hdr10Video = """
        {"index":0,"codec_type":"video","codec_name":"hevc","profile":"Main 10","color_transfer":"smpte2084",
         "width":1920,"height":1080,"avg_frame_rate":"24000/1001",
         "side_data_list":[{"side_data_type":"Mastering display metadata"}],
         "tags":{"language":"eng"},"disposition":{"default":1,"forced":0}}
        """;

    [Fact]
    public void Reads_the_container_from_the_extension_not_the_demuxer_list()
    {
        // ffprobe reports "matroska,webm" — the demuxer's format list, not the container.
        var probe = FfprobeMediaInspector.Map(Json(Hdr10Video), "/media/TRON Legacy (2010).mkv");
        Assert.Equal("mkv", Assert.IsType<ProbeResponse>(probe).Container);
    }

    [Fact]
    public void Reads_the_overall_figures()
    {
        var probe = FfprobeMediaInspector.Map(Json(Hdr10Video), "/media/x.mkv")!;
        Assert.Equal(7506.291, probe.DurationSeconds!.Value, 3);
        Assert.Equal(7699019, probe.Bitrate);
        Assert.Equal(7223885097, probe.SizeBytes);
    }

    [Fact]
    public void Maps_a_video_stream()
    {
        var stream = Assert.Single(FfprobeMediaInspector.Map(Json(Hdr10Video), "/media/x.mkv")!.Streams);
        Assert.Equal(ProbedStreamKind.Video, stream.Kind);
        Assert.Equal("hevc", stream.Codec);
        Assert.Equal("Main 10", stream.Profile);
        Assert.Equal("eng", stream.Language);
        Assert.True(stream.IsDefault);
        Assert.False(stream.IsForced);
        Assert.Equal(1920, stream.Width);
        Assert.Equal(23.976, stream.FrameRate!.Value, 3);
    }

    [Theory]
    // The transfer function decides, and a Dolby Vision record outranks it.
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"smpte2084"}""", HdrFormat.Hdr10)]
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"arib-std-b67"}""", HdrFormat.Hlg)]
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"bt709"}""", HdrFormat.Sdr)]
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"unknown"}""", HdrFormat.Unknown)]
    [InlineData("""{"index":0,"codec_type":"video"}""", HdrFormat.Unknown)]
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"smpte2084","side_data_list":[{"side_data_type":"DOVI configuration record"}]}""", HdrFormat.DolbyVision)]
    [InlineData("""{"index":0,"codec_type":"video","color_transfer":"smpte2084","side_data_list":[{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]}""", HdrFormat.Hdr10Plus)]
    public void Determines_hdr_from_the_transfer_function_and_side_data(string stream, HdrFormat expected) =>
        Assert.Equal(expected, Assert.Single(FfprobeMediaInspector.Map(Json(stream), "/media/x.mkv")!.Streams).Hdr);

    [Fact]
    public void A_file_with_mastering_metadata_but_no_dynamic_layer_is_plain_hdr10()
    {
        // TRON Legacy: PQ with static mastering-display metadata and nothing above it.
        var stream = Assert.Single(FfprobeMediaInspector.Map(Json(Hdr10Video), "/media/x.mkv")!.Streams);
        Assert.Equal(HdrFormat.Hdr10, stream.Hdr);
    }

    [Fact]
    public void Only_video_streams_carry_an_hdr_answer()
    {
        const string Audio = """{"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"sample_rate":"48000"}""";
        var stream = Assert.Single(FfprobeMediaInspector.Map(Json(Audio), "/media/x.mkv")!.Streams);
        Assert.Equal(ProbedStreamKind.Audio, stream.Kind);
        Assert.Equal(HdrFormat.Unknown, stream.Hdr);
        Assert.Equal(6, stream.Channels);
        Assert.Equal(48000, stream.SampleRate);
    }

    [Fact]
    public void Embedded_cover_art_keeps_its_index_so_the_numbering_matches_ffprobe()
    {
        // "The Rock (1996).m4v" has ten trak boxes but eleven ffprobe streams: the artwork is synthesized
        // at index 1, moving every audio track by one. A consumer addresses streams by these indexes when
        // it creates a job, so dropping the entry here would silently select the wrong track.
        const string Streams = """
            {"index":0,"codec_type":"video","codec_name":"h264","disposition":{"attached_pic":0}},
            {"index":1,"codec_type":"video","codec_name":"png","disposition":{"attached_pic":1}},
            {"index":2,"codec_type":"audio","codec_name":"aac","tags":{"language":"eng"}},
            {"index":3,"codec_type":"audio","codec_name":"aac","tags":{"language":"rus"}}
            """;
        var probe = FfprobeMediaInspector.Map(Json(Streams), "/media/The Rock (1996).m4v")!;
        Assert.Equal(4, probe.Streams.Count);
        Assert.Equal([0, 1, 2, 3], probe.Streams.Select(stream => stream.Index));
        Assert.Equal("rus", probe.Streams[3].Language);
    }

    [Theory]
    // Matroska keeps a track name in "title"; MP4 keeps it in udta/name, which ffprobe surfaces as "name".
    [InlineData("""{"index":0,"codec_type":"audio","tags":{"title":"MVO заКАДРЫ"}}""", "MVO заКАДРЫ")]
    [InlineData("""{"index":0,"codec_type":"audio","tags":{"name":"Russian Gavrilov Stereo"}}""", "Russian Gavrilov Stereo")]
    [InlineData("""{"index":0,"codec_type":"audio","tags":{}}""", null)]
    public void Takes_the_track_name_from_whichever_tag_the_container_uses(string stream, string? expected) =>
        Assert.Equal(expected, Assert.Single(FfprobeMediaInspector.Map(Json(stream), "/media/x.mkv")!.Streams).Title);

    [Fact]
    public void An_undefined_language_is_no_language()
    {
        const string Stream = """{"index":0,"codec_type":"audio","tags":{"language":"und"}}""";
        Assert.Null(Assert.Single(FfprobeMediaInspector.Map(Json(Stream), "/media/x.mkv")!.Streams).Language);
    }

    [Theory]
    // 0/0 is ffprobe's "unknown" rational.
    [InlineData("""{"index":0,"codec_type":"video","avg_frame_rate":"0/0"}""")]
    [InlineData("""{"index":0,"codec_type":"video","avg_frame_rate":"nonsense"}""")]
    public void An_unreadable_frame_rate_is_null(string stream) =>
        Assert.Null(Assert.Single(FfprobeMediaInspector.Map(Json(stream), "/media/x.mkv")!.Streams).FrameRate);

    [Fact]
    public void A_stream_kind_this_api_does_not_model_still_occupies_its_index()
    {
        const string Streams = """
            {"index":0,"codec_type":"video"},
            {"index":1,"codec_type":"data"},
            {"index":2,"codec_type":"subtitle","codec_name":"subrip","disposition":{"forced":1}}
            """;
        var probe = FfprobeMediaInspector.Map(Json(Streams), "/media/x.mkv")!;
        Assert.Equal(ProbedStreamKind.Other, probe.Streams[1].Kind);
        Assert.Equal(ProbedStreamKind.Subtitle, probe.Streams[2].Kind);
        Assert.True(probe.Streams[2].IsForced);
    }

    [Fact]
    public void Output_without_streams_is_not_a_probe_result() =>
        Assert.Null(FfprobeMediaInspector.Map("""{"format":{}}""", "/media/x.mkv"));
}
