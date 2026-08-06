using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Covers re-encoding kept audio tracks — the lever that shrinks a file without touching a frame of video.
/// The behaviour that matters is selectivity: a job names one track and the others must still be copied
/// byte-for-byte, because the file this feature exists for holds both lossless dubs worth re-encoding and an
/// original Atmos track that must not be.
/// </summary>
public sealed class AudioTargetArgumentTests
{
    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private static TranscodeJob Job(
        IReadOnlyList<int>? audio,
        IReadOnlyList<AudioTarget>? targets,
        bool copyVideo = false) =>
        new(
            "job-1",
            new TranscodeJobRequest(
                "/in/movie.mkv", "/out/movie.mkv", TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                CopyVideo: copyVideo, AudioStreamIndexes: audio, AudioTargets: targets),
            durationSeconds: null);

    private static string? ValueAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    [Fact]
    public void NoTargets_KeepsTheBlanketCopy()
    {
        // The un-enumerated form is the only one that works with a "copy every audio stream" mapping, so it
        // has to survive untouched.
        var args = Engine().BuildArguments(Job(audio: null, targets: null), TranscodeHardware.None);

        Assert.Equal("copy", ValueAfter(args, "-c:a"));
        Assert.DoesNotContain("-c:a:0", args);
    }

    [Fact]
    public void OneTarget_ReEncodesThatTrackAndCopiesTheRest()
    {
        var args = Engine().BuildArguments(
            Job([3, 5, 7], [new AudioTarget(0, 5, TranscodeAudioCodec.Eac3, 640)]),
            TranscodeHardware.None);

        Assert.Equal("copy", ValueAfter(args, "-c:a:0"));
        Assert.Equal("eac3", ValueAfter(args, "-c:a:1"));
        Assert.Equal("640k", ValueAfter(args, "-b:a:1"));
        Assert.Equal("copy", ValueAfter(args, "-c:a:2"));
        // The blanket form must be gone: leaving it in would fight the per-position arguments.
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void OmittedBitrate_EmitsNone_SoFfmpegScalesItToTheChannelCount()
    {
        // ffmpeg's own default tracks the layout (448k for 5.1, 192k stereo, 96k mono). Substituting one
        // number here would either starve a 5.1 dub or waste bits on a mono commentary.
        var args = Engine().BuildArguments(
            Job([1], [new AudioTarget(0, 1, TranscodeAudioCodec.Eac3)]),
            TranscodeHardware.None);

        Assert.Equal("eac3", ValueAfter(args, "-c:a:0"));
        Assert.DoesNotContain("-b:a:0", args);
    }

    [Fact]
    public void NoChannelArgumentIsEmitted()
    {
        // ffmpeg negotiates the downmix with the encoder (7.1 lands as 5.1). Forcing -ac would also upmix a
        // stereo track, which nobody asked for.
        var args = Engine().BuildArguments(
            Job([1], [new AudioTarget(0, 1, TranscodeAudioCodec.Eac3, 640)]),
            TranscodeHardware.None);

        Assert.DoesNotContain("-ac", args);
        Assert.DoesNotContain("-ac:a:0", args);
    }

    [Fact]
    public void AudioTargets_WorkAlongsideACopiedVideo()
    {
        // The cheapest real conversion there is: shrink the audio, leave every frame of the picture alone.
        var args = Engine().BuildArguments(
            Job([1, 2], [new AudioTarget(0, 1, TranscodeAudioCodec.Eac3, 640)], copyVideo: true),
            TranscodeHardware.None);

        Assert.Equal("copy", ValueAfter(args, "-c:v"));
        Assert.Equal("eac3", ValueAfter(args, "-c:a:0"));
        Assert.Equal("copy", ValueAfter(args, "-c:a:1"));
    }

    [Fact]
    public void Ac3_MapsToItsOwnEncoder()
    {
        var args = Engine().BuildArguments(
            Job([1], [new AudioTarget(0, 1, TranscodeAudioCodec.Ac3, 448)]),
            TranscodeHardware.None);

        Assert.Equal("ac3", ValueAfter(args, "-c:a:0"));
    }
}
