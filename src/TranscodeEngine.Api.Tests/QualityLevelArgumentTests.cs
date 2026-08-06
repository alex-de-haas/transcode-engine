using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Pins the quality level onto each encoder family's rate control. These assertions are the contract the
/// level makes: a job that falls back from one encoder to another keeps asking for the same picture, and no
/// family is left to the driver's default — which is what made a smaller file impossible to ask for.
/// </summary>
public sealed class QualityLevelArgumentTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJob Job(TranscodeQualityLevel? level, TranscodeVideoCodec codec = TranscodeVideoCodec.Hevc) =>
        new(
            "job-1",
            new TranscodeJobRequest("/in/movie.mkv", "/out/movie - HEVC.mkv", codec, TranscodeHardware.None, level),
            durationSeconds: null);

    private static string? ValueAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    [Theory]
    [InlineData(TranscodeQualityLevel.Highest, "18")]
    [InlineData(TranscodeQualityLevel.High, "20")]
    [InlineData(TranscodeQualityLevel.Balanced, "22")]
    [InlineData(TranscodeQualityLevel.Small, "24")]
    public void Software_MapsLevelToCrf(TranscodeQualityLevel level, string expected)
    {
        var args = Engine().BuildArguments(Job(level), TranscodeHardware.None);

        Assert.Equal("libx265", ValueAfter(args, "-c:v"));
        Assert.Equal(expected, ValueAfter(args, "-crf"));
    }

    [Theory]
    [InlineData(TranscodeQualityLevel.Highest, "22")]
    [InlineData(TranscodeQualityLevel.High, "24")]
    [InlineData(TranscodeQualityLevel.Balanced, "25")]
    [InlineData(TranscodeQualityLevel.Small, "26")]
    public void Amf_MapsLevelToConstantQp(TranscodeQualityLevel level, string expected)
    {
        var args = Engine().BuildArguments(Job(level), TranscodeHardware.Amf);

        Assert.Equal("hevc_amf", ValueAfter(args, "-c:v"));
        // CQP is the only quality-style mode this encoder offers; qvbr/hqvbr/vbr_peak all fail its init.
        Assert.Equal("cqp", ValueAfter(args, "-rc"));
        Assert.Equal(expected, ValueAfter(args, "-qp_i"));
        Assert.Equal(expected, ValueAfter(args, "-qp_p"));
    }

    [Fact]
    public void Vaapi_MapsLevelToConstantQp()
    {
        var args = Engine().BuildArguments(Job(TranscodeQualityLevel.Balanced), TranscodeHardware.Vaapi);

        Assert.Equal("hevc_vaapi", ValueAfter(args, "-c:v"));
        Assert.Equal("CQP", ValueAfter(args, "-rc_mode"));
        Assert.Equal("25", ValueAfter(args, "-qp"));
    }

    [Fact]
    public void VideoToolbox_MapsLevelToQuality_OnAnInvertedScale()
    {
        // Every other family counts down from best; VideoToolbox counts up, so a lower level must produce a
        // *lower* number here. Getting this backwards would silently encode "small" at the best quality.
        var highest = Engine().BuildArguments(Job(TranscodeQualityLevel.Highest), TranscodeHardware.VideoToolbox);
        var small = Engine().BuildArguments(Job(TranscodeQualityLevel.Small), TranscodeHardware.VideoToolbox);

        Assert.Equal("hevc_videotoolbox", ValueAfter(highest, "-c:v"));
        Assert.True(int.Parse(ValueAfter(highest, "-q:v")!) > int.Parse(ValueAfter(small, "-q:v")!));
    }

    [Fact]
    public void OmittedLevel_FallsBackToTheDefault()
    {
        // The default is the measured point where software and hardware come out equal, so omitting the
        // level has to land there rather than on whatever the encoder would have chosen.
        var omitted = Engine().BuildArguments(Job(null), TranscodeHardware.None);
        var explicitHigh = Engine().BuildArguments(Job(TranscodeQualityLevel.High), TranscodeHardware.None);

        Assert.Equal(ValueAfter(explicitHigh, "-crf"), ValueAfter(omitted, "-crf"));
    }

    [Fact]
    public void H264_AsksForMoreQualityThanHevc_AtTheSameLevel()
    {
        // x264 needs a lower CRF than x265 for the same picture; sharing one number would quietly make every
        // H.264 output worse than the level promises.
        var hevc = Engine().BuildArguments(Job(TranscodeQualityLevel.High), TranscodeHardware.None);
        var h264 = Engine().BuildArguments(Job(TranscodeQualityLevel.High, TranscodeVideoCodec.H264), TranscodeHardware.None);

        Assert.True(int.Parse(ValueAfter(h264, "-crf")!) < int.Parse(ValueAfter(hevc, "-crf")!));
    }

    [Fact]
    public void CopiedVideo_CarriesNoRateControl()
    {
        var job = new TranscodeJob(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", "/out/movie.mkv", TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                CopyVideo: true),
            durationSeconds: null);

        var args = Engine().BuildArguments(job, TranscodeHardware.None);

        Assert.Equal("copy", ValueAfter(args, "-c:v"));
        Assert.DoesNotContain("-crf", args);
    }
}
