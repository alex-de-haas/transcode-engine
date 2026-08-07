using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Argument construction for an extraction — a job that writes each named stream to its own file instead of
/// composing one output. The three things every composed job emits unconditionally (the video map, a
/// <c>-c:v</c>, and hardware decode setup) must all be absent, and <c>-progress</c> has to move ahead of the
/// first output because it is global rather than per-output.
/// </summary>
public sealed class ExtractJobArgumentTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJob Extract(params ExtractionOutput[] outputs) =>
        new(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", null, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null, Outputs: outputs),
            durationSeconds: null);

    [Fact]
    public void BuildArguments_WritesOneStreamToOneFile()
    {
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.mka", 1)), TranscodeHardware.None);

        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-y",
                "-i", "/in/movie.mkv",
                "-progress", "pipe:1", "-nostats",
                "-map", "0:1", "-c", "copy", "/out/movie.rus.mka",
            ],
            args);
    }

    [Fact]
    public void BuildArguments_MapsNoVideoAndNamesNoVideoCodec()
    {
        // The two arguments a composed job always emits. Nothing here decodes or encodes a picture, and a
        // -map 0:v:0 would put the whole film into every extracted track's file.
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.mka", 1)), TranscodeHardware.None);

        Assert.DoesNotContain("0:v:0", args);
        Assert.DoesNotContain("-c:v", args);
    }

    [Fact]
    public void BuildArguments_SetsUpNoHardwareDecode_EvenWhenTheWorkerResolvedSome()
    {
        // The worker resolves a hardware family for every job; an extraction must ignore whatever it picked,
        // because there is no decode to accelerate and the flags would only cost a device init.
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.mka", 1)), TranscodeHardware.Vaapi);

        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-hwaccel", args);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void BuildArguments_EmitsProgressBeforeTheFirstOutput()
    {
        // -progress is a global option. On the composed path it happens to sit just before the single output;
        // with several outputs it has to precede the first, or ffmpeg reads it as an option of the second.
        var args = Engine().BuildArguments(
            Extract(
                new ExtractionOutput("/out/movie.rus.mka", 1),
                new ExtractionOutput("/out/movie.rus.srt", 3)),
            TranscodeHardware.None);

        Assert.True(args.IndexOf("-progress") < args.IndexOf("/out/movie.rus.mka"));
        Assert.True(args.IndexOf("-nostats") < args.IndexOf("/out/movie.rus.mka"));
    }

    [Fact]
    public void BuildArguments_GivesEachOutputItsOwnMapCodecAndFile()
    {
        var args = Engine().BuildArguments(
            Extract(
                new ExtractionOutput("/out/movie.rus.mka", 1),
                new ExtractionOutput("/out/movie.eng.mka", 2),
                new ExtractionOutput("/out/movie.rus.srt", 3)),
            TranscodeHardware.None);

        Assert.Equal(
            ["-map", "0:1", "-c", "copy", "/out/movie.rus.mka",
             "-map", "0:2", "-c", "copy", "/out/movie.eng.mka",
             "-map", "0:3", "-c", "copy", "/out/movie.rus.srt"],
            args[8..]);
    }

    [Fact]
    public void BuildArguments_NamesATextSubtitleEncoder_WhenTheOutputAsksForOne()
    {
        // The one conversion an extraction performs: a subtitle codec with no file form of its own has to
        // become one to be extracted at all.
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.srt", 3, ExtractionCodec.Srt)), TranscodeHardware.None);

        Assert.Equal(["-map", "0:3", "-c:s", "srt", "/out/movie.rus.srt"], args[8..]);
    }

    [Theory]
    [InlineData(ExtractionCodec.Srt, "srt")]
    [InlineData(ExtractionCodec.Ass, "ass")]
    [InlineData(ExtractionCodec.WebVtt, "webvtt")]
    public void BuildArguments_MapsEveryTextTargetOntoItsEncoder(ExtractionCodec codec, string expected)
    {
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.sub", 3, codec)), TranscodeHardware.None);

        Assert.Equal(expected, args[args.IndexOf("-c:s") + 1]);
    }

    [Fact]
    public void BuildArguments_WritesLanguageAndTitleOntoTheOutputsOnlyStream()
    {
        // One stream per file is what makes -metadata:s:0 unambiguous — there is no position to compute.
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.mka", 1, ExtractionCodec.Copy, "rus", "AniDUB")),
            TranscodeHardware.None);

        Assert.Equal(
            ["-map", "0:1", "-c", "copy",
             "-metadata:s:0", "language=rus",
             "-metadata:s:0", "title=AniDUB",
             "/out/movie.rus.mka"],
            args[8..]);
    }

    [Fact]
    public void BuildArguments_WritesNoMetadataFieldTheRequestLeftNull()
    {
        // A null field keeps the source stream's own tag, so extracting an already-tagged .mka track never
        // silently unlabels it.
        var args = Engine().BuildArguments(
            Extract(new ExtractionOutput("/out/movie.rus.mka", 1, ExtractionCodec.Copy, Language: null, Title: "AniDUB")),
            TranscodeHardware.None);

        Assert.DoesNotContain(args, arg => arg.StartsWith("language=", StringComparison.Ordinal));
        Assert.Contains("title=AniDUB", args);
    }

    [Fact]
    public void BuildArguments_WritesEachOutputToItsOwnDestination()
    {
        // The engine writes temps beside each destination and publishes them only on a clean exit, so the
        // destinations have to line up with the outputs one for one.
        var args = Engine().BuildArguments(
            Extract(
                new ExtractionOutput("/out/movie.rus.mka", 1),
                new ExtractionOutput("/out/movie.rus.srt", 3)),
            TranscodeHardware.None,
            ["/out/.movie.rus.job-1.part.mka", "/out/.movie.rus.job-1.part.srt"]);

        Assert.Equal("/out/.movie.rus.job-1.part.mka", args[12]);
        Assert.Equal("/out/.movie.rus.job-1.part.srt", args[^1]);
        Assert.DoesNotContain("/out/movie.rus.mka", args);
    }

    [Fact]
    public void BuildArguments_RefusesDestinationsThatDoNotLineUpWithTheOutputs()
    {
        // Reading past the end of a shorter list would silently write one output over another's destination,
        // so the mismatch is named rather than discovered as an unexplained job failure.
        var job = Extract(
            new ExtractionOutput("/out/a.mka", 1),
            new ExtractionOutput("/out/b.srt", 3));

        var error = Assert.Throws<ArgumentException>(
            () => Engine().BuildArguments(job, TranscodeHardware.None, ["/out/.a.part.mka"]));

        Assert.Contains("1 destination(s) for 2 output(s)", error.Message);
    }

    [Fact]
    public void OutputPaths_ListsEveryFileAnExtractionProduces()
    {
        var request = new TranscodeJobRequest(
            "/in/movie.mkv", null, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
            Outputs: [new ExtractionOutput("/out/a.mka", 1), new ExtractionOutput("/out/b.srt", 3)]);

        Assert.True(request.IsExtraction);
        Assert.Equal(["/out/a.mka", "/out/b.srt"], request.OutputPaths);
    }

    [Fact]
    public void OutputPaths_IsTheComposedOutputForAnOrdinaryJob()
    {
        var request = new TranscodeJobRequest(
            "/in/movie.mkv", "/out/movie - HEVC.mkv", TranscodeVideoCodec.Hevc, TranscodeHardware.None, null);

        Assert.False(request.IsExtraction);
        Assert.Equal(["/out/movie - HEVC.mkv"], request.OutputPaths);
    }
}
