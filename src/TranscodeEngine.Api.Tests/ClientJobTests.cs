using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class ClientJobTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("client-job-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private FfmpegTranscodeEngine Engine() => new(new() { AppDataDir = _root,
        MediaRoots = new Dictionary<string, string>(), FfprobePath = Path.Combine(_root, "no-probe") }, NullLogger<FfmpegTranscodeEngine>.Instance);
    private TranscodeJobRequest Request() => new(Path.Combine(_root, "input.mkv"), Path.Combine(_root, "output.mkv"),
        TranscodeVideoCodec.H264, TranscodeHardware.None, TranscodeQualityLevel.Small, CopyVideo: true,
        ClientJobId: Guid.NewGuid().ToString("N"));

    [JoinFfmpegFact]
    public async Task Real_encode_and_remux_retain_completed_results_across_restart_and_delete_cleanly()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH")!;
        var ffprobe = Environment.GetEnvironmentVariable("FFPROBE_PATH")!;
        var input = Path.Combine(_root, "original.avi");
        await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc=size=96x64:rate=12", "-t", "1", "-c:v", "mpeg4", input);
        var original = await File.ReadAllBytesAsync(input);
        var settings = new TranscodeEngineSettings { AppDataDir = Path.Combine(_root, "data"), MediaRoots = new Dictionary<string, string>(),
            FfmpegPath = ffmpeg, FfprobePath = ffprobe, DefaultHardware = TranscodeHardware.None };
        var requests = new[] { false, true }.Select(copy => Request() with { InputPath = input,
            OutputPath = Path.Combine(_root, copy ? "remux.mkv" : "encoded.mkv"), CopyVideo = copy }).ToArray();
        using (var first = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance))
        {
            await first.StartAsync(default);
            try
            {
                foreach (var request in requests)
                {
                    var descriptors = await Task.WhenAll(first.CreateAsync(request, default), first.CreateAsync(request, default));
                    Assert.Equal(descriptors[0].JobId, descriptors[1].JobId);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (first.GetSnapshot(request.ClientJobId!)!.State is "Queued" or "Running")
                        await Task.Delay(30, deadline.Token);
                    Assert.Equal("Completed", first.GetSnapshot(request.ClientJobId!)!.State);
                    await Run(ffmpeg, "-v", "error", "-i", request.OutputPath!, "-f", "null", "-");
                }
            }
            finally { await first.StopAsync(default); }
        }
        using var restarted = new FfmpegTranscodeEngine(settings, NullLogger<FfmpegTranscodeEngine>.Instance);
        await restarted.StartAsync(default);
        try
        {
            foreach (var request in requests)
            {
                Assert.Equal("Completed", restarted.GetSnapshot(request.ClientJobId!)!.State);
                var before = await File.ReadAllBytesAsync(request.OutputPath!);
                Assert.Equal(request.ClientJobId, (await restarted.CreateAsync(request, default)).JobId);
                Assert.Equal(before, await File.ReadAllBytesAsync(request.OutputPath!));
                await restarted.RemoveAsync(request.ClientJobId!, true, default);
                Assert.Null(restarted.GetSnapshot(request.ClientJobId!));
                Assert.False(File.Exists(request.OutputPath));
            }
            Assert.Empty(Directory.GetFiles(Path.Combine(settings.AppDataDir, "client-jobs")));
            Assert.Equal(original, await File.ReadAllBytesAsync(input));
        }
        finally { await restarted.StopAsync(default); }
    }

    private static async Task Run(string file, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    [Fact]
    public async Task Concurrent_retries_have_one_job_and_changed_options_conflict()
    {
        using var engine = Engine();
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => engine.CreateAsync(request, default)));
        Assert.All(results, result => Assert.Equal(request.ClientJobId, result.JobId));
        Assert.Single(engine.GetAllSnapshots());
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(request with { MaxHeight = 240 }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RemoveAsync(request.ClientJobId!, false, default));
        await engine.CancelAsync(request.ClientJobId!, default);
        Assert.Equal("Cancelled", engine.GetSnapshot(request.ClientJobId!)!.State);
        await engine.RemoveAsync(request.ClientJobId!, false, default);
        Assert.Null(engine.GetSnapshot(request.ClientJobId!));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "client-jobs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_retains_acceptance_without_starting_a_second_writer(bool cancel)
    {
        var request = Request();
        using (var first = Engine())
        {
            await first.CreateAsync(request, default);
            if (cancel) await first.CancelAsync(request.ClientJobId!, default);
        }
        using var restarted = Engine();
        var result = await restarted.CreateAsync(request, default);
        Assert.Equal(request.ClientJobId, result.JobId);
        Assert.Equal(cancel ? "Cancelled" : "Failed", restarted.GetSnapshot(result.JobId)!.State);
        Assert.Single(restarted.GetAllSnapshots());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("00000000000000000000000000000000")]
    public async Task Invalid_id_is_rejected_without_files(string id)
    {
        using var engine = Engine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(Request() with { ClientJobId = id }, default));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Queue_rejection_does_not_leave_an_acceptance_record()
    {
        using var engine = Engine();
        await engine.StopAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateAsync(Request(), default));
        Assert.Empty(engine.GetAllSnapshots());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "client-jobs")));
    }
}
