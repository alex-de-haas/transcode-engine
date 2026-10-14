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
    private static System.Collections.Concurrent.ConcurrentDictionary<string, TranscodeJob> Jobs(FfmpegTranscodeEngine engine) =>
        (System.Collections.Concurrent.ConcurrentDictionary<string, TranscodeJob>)typeof(FfmpegTranscodeEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;

    private static void Save(FfmpegTranscodeEngine engine, TranscodeJob job) => typeof(FfmpegTranscodeEngine)
        .GetMethod("SaveJoin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(engine, [job]);

    [Fact]
    public async Task Case_insensitive_lookup_and_removal_survive_eviction_and_restart()
    {
        var request = Request();
        using (var engine = Engine())
        {
            Assert.Equal(request.ClientJobId, (await engine.CreateAsync(request with { ClientJobId = request.ClientJobId!.ToUpperInvariant() }, default)).JobId);
            await engine.CancelAsync(request.ClientJobId!, default);
            Jobs(engine).Clear(); // Simulate expiry of the memory-only terminal cache.
            Assert.Equal("Cancelled", engine.GetSnapshot(request.ClientJobId!.ToUpperInvariant())!.State);
            await engine.RemoveAsync(request.ClientJobId!.ToUpperInvariant(), false, default);
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "client-jobs")));
        }
        using var restarted = Engine();
        Assert.Null(restarted.GetSnapshot(request.ClientJobId!));
    }

    [Fact]
    public async Task Late_terminal_save_cannot_resurrect_removed_or_replace_reused_id()
    {
        using var engine = Engine();
        var request = Request();
        await engine.CreateAsync(request, default);
        var old = Jobs(engine)[request.ClientJobId!];
        await engine.CancelAsync(old.JobId, default);
        await engine.RemoveAsync(old.JobId, false, default);
        Save(engine, old);
        Assert.Null(engine.GetSnapshot(old.JobId));
        var replacement = request with { MaxHeight = 240 };
        await engine.CreateAsync(replacement, default);
        Save(engine, old);
        using var restored = Engine();
        Assert.Equal(old.JobId, (await restored.CreateAsync(replacement, default)).JobId);
    }

    [Fact]
    public async Task Client_publication_does_not_overwrite_a_file_created_after_admission()
    {
        using var engine = Engine();
        var request = Request();
        await engine.CreateAsync(request, default);
        var temp = Path.Combine(_root, "finished.mkv");
        await File.WriteAllTextAsync(temp, "encoded");
        await File.WriteAllTextAsync(request.OutputPath!, "other owner");
        Assert.False(engine.TryPublishOutputs(Jobs(engine)[request.ClientJobId!], [temp], [request.OutputPath!], new FfmpegTranscodeEngine.StderrTail()));
        Assert.Equal("other owner", await File.ReadAllTextAsync(request.OutputPath!));
        Assert.Equal("Failed", engine.GetSnapshot(request.ClientJobId!)!.State);
        await engine.RemoveAsync(request.ClientJobId!, true, default);
        Assert.Equal("other owner", await File.ReadAllTextAsync(request.OutputPath!));
    }

    [Theory]
    [InlineData("client", "join")]
    [InlineData("client", "bluray")]
    [InlineData("join", "client")]
    [InlineData("join", "bluray")]
    [InlineData("bluray", "client")]
    [InlineData("bluray", "join")]
    public async Task Durable_ids_conflict_across_operation_types_after_eviction(string existingKind, string incomingKind)
    {
        var original = Request();
        TranscodeJobRequest OfKind(string kind) => kind switch
        {
            "join" => original with { JoinPaths = [original.InputPath, Path.Combine(_root, "part2.mkv")] },
            "bluray" => original with { Bluray = new("revision", "00001", 0, [], []) },
            _ => original
        };
        var existing = OfKind(existingKind);
        object record = existingKind switch
        {
            "join" => new { Request = existing, Parts = new[] { new JoinMediaInfo(original.InputPath, 1, DateTime.UtcNow, 1, 0, [], []) }, State = JobState.Cancelled, Error = (string?)null },
            "bluray" => new { Request = existing, Playlist = new TranscodeEngine.Api.Bluray.BlurayPlaylist("00001", 1, 0, [], []), State = JobState.Cancelled, Error = (string?)null },
            _ => new { Request = existing, Duration = 1, State = JobState.Cancelled, Error = (string?)null }
        };
        var directory = Directory.CreateDirectory(Path.Combine(_root, existingKind + "-jobs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, original.ClientJobId + ".json"), System.Text.Json.JsonSerializer.Serialize(record));
        using var engine = Engine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAsync(OfKind(incomingKind) with { OutputPath = Path.Combine(_root, "different.mkv") }, default));
        Assert.Single(Directory.GetDirectories(_root));
        Assert.Equal("Cancelled", engine.GetSnapshot(original.ClientJobId!)!.State);
    }

    [Fact]
    public async Task Interrupted_Dolby_Vision_removes_all_scratch_files_and_preserves_originals()
    {
        var request = Request() with { DolbyVision = DolbyVisionMode.ToProfile81 };
        using (var engine = Engine()) await engine.CreateAsync(request, default);
        var temps = FfmpegTranscodeEngine.DolbyVisionIntermediatePaths(request.OutputPath!, request.ClientJobId!);
        var paths = new[] { temps.Tracks, temps.SourceLayers, temps.ConvertedLayer,
            Path.Combine(_root, $".output.{request.ClientJobId}.part.mkv") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, "scratch");
        await File.WriteAllTextAsync(request.InputPath, "original");
        using var restarted = Engine();
        Assert.Equal("Failed", restarted.GetSnapshot(request.ClientJobId!)!.State);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal("original", await File.ReadAllTextAsync(request.InputPath));
    }

    [Fact]
    public async Task Dolby_Vision_worker_failure_retains_actual_terminal_error_after_restart()
    {
        var request = Request() with { DolbyVision = DolbyVisionMode.ToProfile81 };
        string? error;
        using (var engine = Engine())
        {
            await engine.CreateAsync(request, default);
            await engine.StartAsync(default);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (engine.GetSnapshot(request.ClientJobId!)!.State is "Queued" or "Running") await Task.Delay(20, deadline.Token);
                error = engine.GetSnapshot(request.ClientJobId!)!.Error;
                Assert.Equal("Failed", engine.GetSnapshot(request.ClientJobId!)!.State);
            }
            finally { await engine.StopAsync(default); }
        }
        using var restarted = Engine();
        Assert.Equal(error, restarted.GetSnapshot(request.ClientJobId!)!.Error);
    }

    [JoinFfmpegFact]
    public async Task Reused_id_ignores_stale_queue_entry_and_cannot_be_removed_during_finalization()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH")!;
        var input = Path.Combine(_root, "original.avi");
        await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc=size=96x64:rate=12", "-t", "1", "-c:v", "mpeg4", input);
        using var engine = new FfmpegTranscodeEngine(new() { AppDataDir = Path.Combine(_root, "data"),
            MediaRoots = new Dictionary<string, string>(), FfmpegPath = ffmpeg,
            FfprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH")!, MaxConcurrentJobs = 2 }, NullLogger<FfmpegTranscodeEngine>.Instance);
        var request = Request() with { InputPath = input };
        await engine.CreateAsync(request, default);
        await engine.CancelAsync(request.ClientJobId!, default);
        await engine.RemoveAsync(request.ClientJobId!, false, default);
        await engine.CreateAsync(request, default);
        var starts = 0;
        var removalBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.JobStarted += (_, _) => Interlocked.Increment(ref starts);
        engine.JobCompleted += (_, id) =>
        {
            try { engine.RemoveAsync(id, true, default).GetAwaiter().GetResult(); removalBlocked.TrySetResult(false); }
            catch (InvalidOperationException) { removalBlocked.TrySetResult(true); }
        };
        await engine.StartAsync(default);
        try { Assert.True(await removalBlocked.Task.WaitAsync(TimeSpan.FromSeconds(20))); }
        finally { await engine.StopAsync(default); }
        Assert.Equal(1, starts);
        Assert.Equal("Completed", engine.GetSnapshot(request.ClientJobId!)!.State);
        await Run(ffmpeg, "-v", "error", "-i", request.OutputPath!, "-f", "null", "-");
        await engine.RemoveAsync(request.ClientJobId!, true, default);
        Assert.Null(engine.GetSnapshot(request.ClientJobId!));
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task Startup_bounds_restored_memory_and_does_not_rewrite_terminal_history()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "client-jobs")).FullName;
        var requests = Enumerable.Range(0, 510).Select(_ => Request()).ToArray();
        var timestamp = DateTime.UtcNow.AddDays(-2);
        foreach (var request in requests)
        {
            var path = Path.Combine(directory, request.ClientJobId + ".json");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new {
                Request = request, Duration = 1, State = JobState.Cancelled, Error = (string?)null }));
            File.SetLastWriteTimeUtc(path, timestamp);
        }
        using var engine = Engine();
        await engine.StartAsync(default);
        try
        {
            Assert.Equal(500, engine.GetAllSnapshots().Count);
            Assert.Equal(510, Directory.GetFiles(directory).Length);
            var restoredIds = engine.GetAllSnapshots().Select(job => job.JobId).ToHashSet();
            var evicted = requests.First(request => !restoredIds.Contains(request.ClientJobId!));
            Assert.Equal("Cancelled", engine.GetSnapshot(evicted.ClientJobId!)!.State);
            Assert.All(Directory.GetFiles(directory), path => Assert.True(Math.Abs((File.GetLastWriteTimeUtc(path) - timestamp).TotalSeconds) < 1));
        }
        finally { await engine.StopAsync(default); }
    }

}
