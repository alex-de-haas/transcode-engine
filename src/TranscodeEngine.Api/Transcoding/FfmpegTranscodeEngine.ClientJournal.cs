using System.Text.Json;

namespace TranscodeEngine.Api.Transcoding;

public sealed partial class FfmpegTranscodeEngine
{
    private sealed record ClientJournal(TranscodeJobRequest Request, double? Duration, JobState State, string? Error);
    private string ClientJournalDirectory => Path.Combine(_settings.AppDataDir, "client-jobs");
    private string ClientJournalPath(string id) => Path.Combine(ClientJournalDirectory, CanonicalJobId(id) + ".json");
    private static bool IsClientJob(TranscodeJobRequest request) =>
        request.ClientJobId is not null && !request.IsJoin && request.Bluray is null && !request.IsExtraction;

    private static string CanonicalJobId(string id) => Guid.TryParseExact(id, "N", out var value) ? value.ToString("N") : id;

    // All durable operations share one ID namespace, even after their memory entry has expired.
    private void RestoreJob(string? id)
    {
        if (id is null || !Guid.TryParseExact(id, "N", out _)) return;
        lock (_joinJournalGate)
        {
            id = CanonicalJobId(id);
            RestoreJoins(id);
            RestoreBlurays(id);
            RestoreClientJobs(id);
        }
    }

    private JobDescriptor? FindExistingClientJob(TranscodeJobRequest request)
    {
        if (!IsClientJob(request)) return null;
        var id = request.ClientJobId!;
        RestoreClientJobs(id);
        if (!_jobs.TryGetValue(id, out var previous)) return null;
        // Compare all selections and encoder options, including ordered lists, not just the output path.
        if (JsonSerializer.Serialize(previous.Request) != JsonSerializer.Serialize(request))
            throw new ArgumentException("This clientJobId already identifies a different transcode request.");
        return new JobDescriptor(id, request.InputPath, request.OutputPath,
            previous.DurationSeconds, TryFileLength(request.InputPath), previous.OutputPaths);
    }

    // Called under the shared journal gate, before queue admission and on terminal transitions.
    private void SaveClientJob(TranscodeJob job)
    {
        if (!IsClientJob(job.Request)) return;
        Directory.CreateDirectory(ClientJournalDirectory);
        var path = ClientJournalPath(job.JobId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new ClientJournal(
            job.Request, job.DurationSeconds, job.State, job.Error)));
        File.Move(temp, path, true);
    }

    private void RestoreClientJobs(string? onlyId = null)
    {
        if (onlyId is not null && !Guid.TryParseExact(onlyId, "N", out _)) return;
        lock (_joinJournalGate)
        {
            if (!Directory.Exists(ClientJournalDirectory)) return;
            foreach (var path in onlyId is null ? Directory.EnumerateFiles(ClientJournalDirectory, "*.json") : new[] { ClientJournalPath(onlyId) })
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (_jobs.ContainsKey(id) || !File.Exists(path)) continue;
                // A corrupt acceptance record must not turn into permission to submit another writer.
                var record = JsonSerializer.Deserialize<ClientJournal>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Invalid transcode acceptance record.");
                if (!Guid.TryParseExact(id, "N", out _) || !string.Equals(record.Request.ClientJobId, id, StringComparison.OrdinalIgnoreCase) || !IsClientJob(record.Request))
                    throw new InvalidDataException("Invalid transcode acceptance identity.");
                var job = new TranscodeJob(id, record.Request, record.Duration);
                if (record.State is JobState.Queued or JobState.Running)
                {
                    job.Fail("The engine restarted before this transcode finished. Start a new job to retry.");
                    foreach (var output in job.OutputPaths) TryDeleteOutput(TempOutputPath(output, id));
                    if (job.Request.ConvertsDolbyVision)
                    {
                        var temps = DolbyVisionIntermediatePaths(job.Request.OutputPath!, id);
                        foreach (var temp in new[] { temps.Tracks, temps.SourceLayers, temps.ConvertedLayer }) TryDeleteOutput(temp);
                    }
                }
                else if (record.State == JobState.Failed) job.Fail(record.Error);
                else job.Complete(record.State);
                _jobs[id] = job;
                if (record.State is JobState.Queued or JobState.Running) SaveClientJob(job);
                if (onlyId is null && _jobs.Count > MaxRetainedTerminalJobs) PruneTerminalJobs();
            }
        }
    }
}
