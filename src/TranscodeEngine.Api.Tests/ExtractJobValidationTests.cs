using TranscodeEngine.Api.Transcoding;
using static TranscodeEngine.Api.Transcoding.FfmpegTranscodeEngine;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// The create-time checks an extraction pays for by probing its input. They live in the engine rather than
/// the endpoint because they need the input's streams — and they are worth running at all because an
/// extraction is disk-bound: a stream index ffmpeg rejects is a typo discovered after reading the whole
/// container.
/// </summary>
public sealed class ExtractJobValidationTests
{
    private static readonly IReadOnlyList<ProbedStream> Streams =
    [
        new(0, "video", "hevc"),
        new(1, "audio", "ac3"),
        new(2, "audio", "truehd"),
        new(3, "subtitle", "subrip"),
        new(4, "subtitle", "hdmv_pgs_subtitle"),
    ];

    [Fact]
    public void ExtractionOutputError_AcceptsAnAudioAndASubtitleStream()
    {
        var error = ExtractionOutputError(
            Streams,
            [new ExtractionOutput("/out/movie.rus.mka", 1), new ExtractionOutput("/out/movie.rus.srt", 3)]);

        Assert.Null(error);
    }

    [Fact]
    public void ExtractionOutputError_RefusesAStreamTheInputDoesNotHave()
    {
        var error = ExtractionOutputError(Streams, [new ExtractionOutput("/out/movie.rus.mka", 9)]);

        Assert.Contains("no stream 9", error);
    }

    [Fact]
    public void ExtractionOutputError_RefusesVideo()
    {
        // Deliberately not offered: a raw elementary video stream is a different problem from a track that
        // is already usable as a file elsewhere.
        var error = ExtractionOutputError(Streams, [new ExtractionOutput("/out/movie.h265", 0)]);

        Assert.Contains("video", error);
        Assert.Contains("only audio and subtitle", error);
    }

    [Fact]
    public void ExtractionOutputError_RefusesABitmapSubtitleAimedAtATextFile()
    {
        // PGS cannot become an .srt by any route ffmpeg offers — turning one into text is OCR.
        var error = ExtractionOutputError(Streams, [new ExtractionOutput("/out/movie.eng.srt", 4)]);

        Assert.Contains("hdmv_pgs_subtitle", error);
        Assert.Contains("movie.eng.srt", error);
    }

    [Theory]
    [InlineData(".ass")]
    [InlineData(".ssa")]
    [InlineData(".vtt")]
    public void ExtractionOutputError_RefusesABitmapSubtitleForEveryTextExtension(string extension)
    {
        var error = ExtractionOutputError(Streams, [new ExtractionOutput($"/out/movie.eng{extension}", 4)]);

        Assert.Contains("picture-based", error);
    }

    [Fact]
    public void ExtractionOutputError_AllowsABitmapSubtitleIntoANonTextContainer()
    {
        // .sup is what a PGS track is; only the text extensions are the contradiction.
        var error = ExtractionOutputError(Streams, [new ExtractionOutput("/out/movie.eng.sup", 4)]);

        Assert.Null(error);
    }

    [Fact]
    public void ExtractionOutputError_RefusesATextCodecOnAnAudioStream()
    {
        var error = ExtractionOutputError(
            Streams, [new ExtractionOutput("/out/movie.rus.mka", 1, ExtractionCodec.Srt)]);

        Assert.Contains("applies only to a subtitle stream", error);
    }

    [Fact]
    public void ExtractionOutputError_ReportsTheFirstProblemOnly()
    {
        var error = ExtractionOutputError(
            Streams,
            [new ExtractionOutput("/out/a.mka", 9), new ExtractionOutput("/out/b.mka", 0)]);

        Assert.Contains("no stream 9", error);
    }

    [Fact]
    public void ParseProbedStreams_ReadsIndexKindAndCodec()
    {
        // ffprobe orders the JSON fields by its own struct layout, not by the -show_entries order, so this is
        // parsed by name.
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video" },
            { "index": 1, "codec_name": "ac3", "codec_type": "audio" },
            { "index": 3, "codec_name": "subrip", "codec_type": "subtitle" }
          ]
        }
        """;

        var streams = ParseProbedStreams(json);

        Assert.Equal(3, streams.Count);
        Assert.Equal(new ProbedStream(0, "video", "h264"), streams[0]);
        Assert.Equal(new ProbedStream(1, "audio", "ac3"), streams[1]);
        Assert.Equal(new ProbedStream(3, "subtitle", "subrip"), streams[2]);
    }

    [Fact]
    public void ParseProbedStreams_SkipsAnEntryWithoutAnIndex()
    {
        const string json = """{ "streams": [ { "codec_type": "audio" }, { "index": 2, "codec_type": "audio", "codec_name": "dts" } ] }""";

        var stream = Assert.Single(ParseProbedStreams(json));

        Assert.Equal(2, stream.Index);
    }

    [Fact]
    public void ParseProbedStreams_FillsInWhatFfprobeOmitted()
    {
        const string json = """{ "streams": [ { "index": 1 } ] }""";

        var stream = Assert.Single(ParseProbedStreams(json));

        Assert.Equal(string.Empty, stream.Kind);
        Assert.Equal(string.Empty, stream.Codec);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "streams": {} }""")]
    public void ParseProbedStreams_AnswersNothingRatherThanThrowing(string json)
    {
        // This feeds a validation check, and failing to parse must not be worse than never having probed:
        // an empty list means "cannot check", and the caller then lets the job through.
        Assert.Empty(ParseProbedStreams(json));
    }
}
