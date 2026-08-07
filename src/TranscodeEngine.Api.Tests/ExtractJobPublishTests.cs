using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Publishing a run that writes several files. ffmpeg writes to a temp beside each destination and the temps
/// are renamed onto the real paths only after a clean exit, so a failed or cancelled run can never truncate a
/// pre-existing file. The set of renames is not atomic, and what happens when one of them fails is a
/// deliberate decision rather than an accident — see the assertions below.
/// </summary>
public sealed class ExtractJobPublishTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("te-publish").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FfmpegTranscodeEngine Engine() =>
        new(
            new TranscodeEngineSettings { AppDataDir = "/tmp/te", MediaRoots = new Dictionary<string, string>() },
            NullLogger<FfmpegTranscodeEngine>.Instance);

    private TranscodeJob Job(params string[] outputPaths) =>
        new(
            "job-1",
            new TranscodeJobRequest(
                Path.Combine(_root, "in.mkv"), null, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                Outputs: outputPaths.Select((path, index) => new ExtractionOutput(path, index + 1)).ToList()),
            durationSeconds: null);

    private string Written(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TryPublishOutputs_MovesEveryTempOntoItsOutput()
    {
        var temps = new[] { Written(".a.part.mka", "dub"), Written(".b.part.srt", "subs") };
        var outputs = new[] { Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt") };
        var job = Job(outputs);

        var published = Engine().TryPublishOutputs(job, temps, outputs, new FfmpegTranscodeEngine.StderrTail());

        Assert.True(published);
        Assert.Equal("dub", File.ReadAllText(outputs[0]));
        Assert.Equal("subs", File.ReadAllText(outputs[1]));
        Assert.All(temps, temp => Assert.False(File.Exists(temp)));
    }

    [Fact]
    public void TryPublishOutputs_ReplacesAnExistingFileAtAnOutputPath()
    {
        // The successful run is the new good result, exactly as it is for a composed output.
        var temp = Written(".a.part.mka", "new");
        var output = Written("a.mka", "old");

        var published = Engine().TryPublishOutputs(Job(output), [temp], [output], new FfmpegTranscodeEngine.StderrTail());

        Assert.True(published);
        Assert.Equal("new", File.ReadAllText(output));
    }

    [Fact]
    public void TryPublishOutputs_FailsTheJobAndKeepsWhatLanded_WhenOneRenameFails()
    {
        // The second output's temp is missing, so its rename throws. The first has already been published,
        // and it stays: it is a file the caller asked for, and deleting it to restore tidiness would destroy
        // the result of work that succeeded. The consumer's contract matches — it records what exists and
        // treats the job as failed.
        var firstTemp = Written(".a.part.mka", "dub");
        var outputs = new[] { Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt") };
        var temps = new[] { firstTemp, Path.Combine(_root, ".b.part.srt") };
        var job = Job(outputs);
        var engine = Engine();
        string? failed = null;
        engine.JobFailed += (_, jobId) => failed = jobId;

        var published = engine.TryPublishOutputs(job, temps, outputs, new FfmpegTranscodeEngine.StderrTail());

        Assert.False(published);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("job-1", failed);
        Assert.Equal("dub", File.ReadAllText(outputs[0]));
        Assert.False(File.Exists(outputs[1]));
    }

    [Fact]
    public void TryPublishOutputs_PublishesNothing_WhenTheFirstRenameFails()
    {
        var outputs = new[] { Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt") };
        var temps = new[] { Path.Combine(_root, ".a.part.mka"), Written(".b.part.srt", "subs") };
        var job = Job(outputs);

        var published = Engine().TryPublishOutputs(job, temps, outputs, new FfmpegTranscodeEngine.StderrTail());

        Assert.False(published);
        Assert.All(outputs, output => Assert.False(File.Exists(output)));
    }

    [Fact]
    public void TryPublishOutputs_RefusesListsThatDoNotLineUp()
    {
        var outputs = new[] { Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt") };

        var error = Assert.Throws<ArgumentException>(() => Engine().TryPublishOutputs(
            Job(outputs), [Written(".a.part.mka")], outputs, new FfmpegTranscodeEngine.StderrTail()));

        Assert.Contains("1 temp path(s) for 2 output(s)", error.Message);
    }

    [Fact]
    public async Task RemoveAsync_WithDeleteOutput_DeletesEveryOutput()
    {
        // An extraction's outputs are one job's result, not several — leaving the rest behind would strand
        // files the caller has just asked to be rid of.
        var input = Written("in.mkv");
        var first = Written("a.mka");
        var second = Written("b.srt");
        var engine = Engine();
        var descriptor = await engine.CreateAsync(
            new TranscodeJobRequest(
                input, null, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                Outputs: [new ExtractionOutput(first, 1), new ExtractionOutput(second, 3)]),
            CancellationToken.None);

        await engine.RemoveAsync(descriptor.JobId, deleteOutput: true, CancellationToken.None);

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task CreateAsync_ReportsEveryOutputPath()
    {
        var input = Written("in.mkv");
        var engine = Engine();

        var descriptor = await engine.CreateAsync(
            new TranscodeJobRequest(
                input, null, TranscodeVideoCodec.Hevc, TranscodeHardware.None, null,
                Outputs:
                [
                    new ExtractionOutput(Path.Combine(_root, "a.mka"), 1),
                    new ExtractionOutput(Path.Combine(_root, "b.srt"), 3),
                ]),
            CancellationToken.None);

        Assert.Null(descriptor.OutputPath);
        Assert.Equal([Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt")], descriptor.OutputPaths);

        // The snapshot names an extraction for what it reads, since no single output represents it, and
        // reports no encoder family because none runs.
        var snapshot = engine.GetSnapshot(descriptor.JobId)!;
        Assert.Equal("in.mkv", snapshot.Name);
        Assert.Equal("none", snapshot.EffectiveHardware);
        Assert.Equal([Path.Combine(_root, "a.mka"), Path.Combine(_root, "b.srt")], snapshot.OutputPaths);
    }
}
