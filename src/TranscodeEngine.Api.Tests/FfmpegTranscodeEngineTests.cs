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
}
