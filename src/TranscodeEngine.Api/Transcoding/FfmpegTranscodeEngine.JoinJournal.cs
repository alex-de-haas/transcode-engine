using System.Text.Json;

namespace TranscodeEngine.Api.Transcoding;

public sealed partial class FfmpegTranscodeEngine
{
    private sealed record JoinJournal(TranscodeJobRequest Request, JoinMediaInfo[] Parts, JobState State, string? Error);
    private string JoinJournalDirectory => Path.Combine(_settings.AppDataDir, "join-jobs");
    private string JoinJournalPath(string id) => Path.Combine(JoinJournalDirectory, id + ".json");

    // Join records survive a process restart so an interrupted writer never becomes an unknown job that
    // its caller might accidentally submit again. Small metadata writes only; media stays on its mount.
    private readonly object _joinJournalGate = new();

    private void SaveJoin(TranscodeJob job)
    {
        lock (_joinJournalGate) SaveJoinCore(job);
    }

    private void SaveJoinCore(TranscodeJob job)
    {
        if (!job.Request.IsJoin || job.JoinParts is null) return;
        Directory.CreateDirectory(JoinJournalDirectory);
        var path = JoinJournalPath(job.JobId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new JoinJournal(job.Request, job.JoinParts, job.State, job.Error)));
        File.Move(temp, path, true);
    }

    private void RestoreJoins(string? onlyId = null)
    {
        if (!Directory.Exists(JoinJournalDirectory)) return;
        foreach (var path in onlyId is null ? Directory.EnumerateFiles(JoinJournalDirectory, "*.json") : new[] { JoinJournalPath(onlyId) })
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (_jobs.ContainsKey(id) || !File.Exists(path)) continue;
                var record = JsonSerializer.Deserialize<JoinJournal>(File.ReadAllText(path));
                if (record is null || !Guid.TryParseExact(id, "N", out _) || !record.Request.IsJoin) continue;
                var job = new TranscodeJob(id, record.Request, record.Parts.Sum(part => part.Duration)) { JoinParts = record.Parts };
                if (record.State is JobState.Queued or JobState.Running)
                {
                    job.Fail("The engine restarted before this join finished. Original parts are unchanged; start a new join to retry.");
                    foreach (var output in job.OutputPaths) TryDeleteOutput(TempOutputPath(output, id));
                    TryDeleteOutput(job.JoinListPath);
                    TryDeleteOutput(job.JoinMetadataPath);
                }
                else if (record.State == JobState.Failed) job.Fail(record.Error);
                else job.Complete(record.State);
                _jobs[id] = job;
                SaveJoin(job);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not restore join journal {Path}.", path);
            }
        }
    }
}
