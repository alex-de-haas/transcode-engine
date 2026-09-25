using System.Text.Json;
using TranscodeEngine.Api.Bluray;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Probing;
using Microsoft.Extensions.Logging.Abstractions;

namespace TranscodeEngine.Api.Transcoding;

public sealed partial class FfmpegTranscodeEngine
{
    private sealed record BlurayJournal(TranscodeJobRequest Request, BlurayPlaylist Playlist, JobState State, string? Error);
    private string BlurayJournalPath(string id) => Path.Combine(_settings.AppDataDir, "bluray-jobs", id + ".json");
    private void SaveBluray(TranscodeJob job)
    {
        if (job.Request.Bluray is null || job.BlurayPlaylist is null) return;
        var path = BlurayJournalPath(job.JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new BlurayJournal(job.Request, job.BlurayPlaylist, job.State, job.Error)));
        File.Move(path + ".tmp", path, true);
    }
    private void RestoreBlurays(string? onlyId = null)
    {
        var directory = Path.GetDirectoryName(BlurayJournalPath("unused"))!;
        if (!Directory.Exists(directory)) return;
        foreach (var path in onlyId is null ? Directory.EnumerateFiles(directory, "*.json") : [BlurayJournalPath(onlyId)])
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(id, "N", out _) || _jobs.ContainsKey(id) || !File.Exists(path)) continue;
                var record = JsonSerializer.Deserialize<BlurayJournal>(File.ReadAllText(path));
                if (record?.Request.Bluray is null) continue;
                var job = new TranscodeJob(id, record.Request, record.Playlist.DurationSeconds) { BlurayPlaylist = record.Playlist };
                if (record.State is JobState.Queued or JobState.Running)
                {
                    job.Fail("The engine restarted before MKV creation finished. The original disc is unchanged; start a new operation to retry.");
                    TryDeleteOutput(TempOutputPath(record.Request.OutputPath!, id));
                }
                else if (record.State == JobState.Failed) job.Fail(record.Error);
                else job.Complete(record.State);
                _jobs[id] = job;
                SaveBluray(job);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { _logger.LogWarning(ex, "Could not restore Blu-ray job {Path}.", path); }
        }
    }

    private JobDescriptor? FindExistingBluray(TranscodeJobRequest request)
    {
        if (request.Bluray is null || request.ClientJobId is not { } id) return null;
        if (!Guid.TryParseExact(id, "N", out var guid) || guid == Guid.Empty) throw new ArgumentException("A Blu-ray job requires a stable clientJobId.");
        if (!_jobs.ContainsKey(id)) RestoreBlurays(id);
        if (!_jobs.TryGetValue(id, out var job)) return null;
        if (JsonSerializer.Serialize(job.Request) != JsonSerializer.Serialize(request))
            throw new ArgumentException("This clientJobId identifies a different operation.");
        return new(id, request.InputPath, request.OutputPath, job.BlurayPlaylist?.DurationSeconds, null, job.OutputPaths);
    }

    internal static void ValidateBluraySelection(BluraySelection selection, BlurayPlaylist playlist)
    {
        if (playlist.Error is not null) throw new ArgumentException(playlist.Error);
        if (selection.Audio is not { Count: > 0 } || selection.Subtitles is null)
            throw new ArgumentException("Select at least one audio track and an explicit subtitle selection.");
        // MKVToolNix combines a Dolby Vision enhancement layer with the primary picture. It must not
        // become a separately selectable PiP track or be removed by an ffmpeg 0:v:0 mapping.
        if (playlist.Tracks.FirstOrDefault(t => t.Type == "video")?.Id != selection.VideoTrackId)
            throw new ArgumentException("Select the primary video track.");
        foreach (var (kind, tracks) in new[] { ("audio", selection.Audio), ("subtitles", selection.Subtitles) })
        {
            if (tracks.Any(t => t is null) || tracks.Select(t => t.Id).Distinct().Count() != tracks.Count || tracks.Count(t => t.Default) > 1)
                throw new ArgumentException("Track selections must be unique with at most one default per type.");
            foreach (var track in tracks)
            {
                if (!playlist.Tracks.Any(t => t.Id == track.Id && t.Type == kind))
                    throw new ArgumentException("A selected track does not belong to this playlist.");
                if (track.Title?.Length > 256 || track.Language?.Length > 35 ||
                    (track.Title?.Any(char.IsControl) ?? false) || (track.Language?.Any(char.IsControl) ?? false))
                    throw new ArgumentException("A track label is too long or contains control characters.");
            }
        }
    }

    internal static List<string> BuildBlurayArguments(string playlistPath, string destination, BluraySelection selection)
    {
        // MKVToolNix otherwise drops a final MPLS chapter shorter than five seconds.
        var args = new List<string> { "--gui-mode", "--engage", "keep_last_chapter_in_mpls",
            "--output", destination, "--video-tracks", selection.VideoTrackId.ToString(),
            "--audio-tracks", string.Join(",", selection.Audio.Select(t => t.Id)), "--no-buttons", "--no-attachments" };
        if (selection.Subtitles.Count == 0) args.Add("--no-subtitles");
        else args.AddRange(["--subtitle-tracks", string.Join(",", selection.Subtitles.Select(t => t.Id))]);
        foreach (var track in selection.Audio.Concat(selection.Subtitles))
        {
            args.AddRange(["--default-track-flag", $"{track.Id}:{(track.Default ? 1 : 0)}",
                "--forced-display-flag", $"{track.Id}:{(track.Forced ? 1 : 0)}"]);
            if (track.Language is { Length: > 0 } language) args.AddRange(["--language", $"{track.Id}:{language}"]);
            if (track.Title is { } title) args.AddRange(["--track-name", $"{track.Id}:{title}"]);
        }
        args.Add(playlistPath);
        return args;
    }

    internal async Task RunBlurayJobAsync(TranscodeJob job, CancellationToken ct)
    {
        var request = job.Request;
        var selection = request.Bluray!;
        var output = request.OutputPath!;
        var temp = TempOutputPath(output, job.JobId);
        var tail = new StderrTail();
        try
        {
            BlurayEndpoints.ValidateDestination(request.InputPath, output);
            var inventory = BlurayInspector.Inventory(request.InputPath);
            if (inventory.Revision != selection.Revision) throw new ArgumentException("The disc changed since inspection; inspect it again.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (AvailableBytes(output) is { } available && available < inventory.Size)
                throw new ArgumentException("Insufficient free space to safely create this MKV while retaining the disc.");
            var start = ToolProcess(_settings.MkvmergePath);
            foreach (var arg in BuildBlurayArguments(BlurayInspector.PlaylistPath(request.InputPath, selection.PlaylistId), temp, selection))
                start.ArgumentList.Add(arg);
            // MKVToolNix reports playlist progress; disc size also includes unrelated titles.
            if (!await RunStageAsync(job, start, "mkvmerge", 0,
                    StageProgress.Growth(temp, null, true), tail, ct, maxAcceptedExitCode: 1,
                    outputProgress: BlurayMuxProgress, completionPercent: 90))
            {
                if (ct.IsCancellationRequested && !job.CancelRequested) MarkBlurayInterrupted(job);
                return;
            }
            job.ReportProgress(90);
            if (BlurayInspector.Inventory(request.InputPath).Revision != selection.Revision)
                throw new ArgumentException("The disc changed during MKV creation; output was not published.");
            var json = await BlurayInspector.IdentifyAsync(_settings.MkvmergePath, temp, ct);
            ValidateBlurayOutput(job.BlurayPlaylist!, selection, json);
            var media = await new FfprobeMediaInspector(_settings, NullLogger<FfprobeMediaInspector>.Instance).InspectAsync(temp, ct);
            ValidateBlurayPicture(job.BlurayPlaylist!.VideoFormats ?? [], media?.Streams.FirstOrDefault(s => s.Kind == ProbedStreamKind.Video));
            if (job.CancelRequested || ct.IsCancellationRequested) { MarkBlurayInterrupted(job); return; }
            PublishBlurayOutput(job, temp, output);
        }
        catch (OperationCanceledException) { MarkBlurayInterrupted(job); }
        catch (Exception ex)
        {
            job.Fail(ex.Message);
            _logger.LogWarning(ex, "Blu-ray job {Id} failed.", job.JobId);
            NotifyBluraySafely(JobFailed, job.JobId);
        }
        finally
        {
            TryDeleteOutput(temp);
            SaveBluraySafely(job);
        }
    }

    internal static double? BlurayMuxProgress(string line)
    {
        const string prefix = "#GUI#progress ";
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(line[prefix.Length..].TrimEnd('%'), out var percent) && percent is >= 0 and <= 100
            ? percent * 0.9 : null;
    }

    internal static void MarkBlurayInterrupted(TranscodeJob job)
    {
        if (job.CancelRequested) job.Complete(JobState.Cancelled);
        else job.Fail("The engine stopped before MKV creation finished. The original disc is unchanged; start a new operation to retry.");
    }

    internal void PublishBlurayOutput(TranscodeJob job, string temp, string output)
    {
        var size = new FileInfo(temp).Length;
        File.Move(temp, output, overwrite: false);
        job.ReportOutputSize(size);
        job.Complete(JobState.Completed);
        // Publication is complete; journal/event failures must not turn it into a failed job.
        SaveBluraySafely(job);
        NotifyBluraySafely(JobCompleted, job.JobId);
    }

    private void SaveBluraySafely(TranscodeJob job)
    {
        try { SaveBluray(job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning(ex, "Could not persist terminal Blu-ray job {Id}; keeping the worker available.", job.JobId); }
    }

    private void NotifyBluraySafely(EventHandler<string>? handlers, string id)
    {
        if (handlers is null) return;
        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try { handler(this, id); }
            catch (Exception ex) { _logger.LogWarning(ex, "Blu-ray job {Id} notification failed.", id); }
        }
    }

    internal static void ValidateBlurayPicture(IReadOnlyList<ProbedStreamInfo> sources, ProbedStreamInfo? output)
    {
        if (sources.Count == 0 || output is null) throw new ArgumentException("Cannot validate the output picture.");
        foreach (var source in sources)
        {
            if (source.Codec != output.Codec || source.Width != output.Width || source.Height != output.Height ||
                source.BitDepth != output.BitDepth ||
                (source.Hdr != output.Hdr && !(source.Hdr == HdrFormat.Hdr10 && output.Hdr == HdrFormat.DolbyVision)) ||
                (source.DolbyVision is not null && source.DolbyVision != output.DolbyVision))
                throw new ArgumentException("The output did not preserve the source picture, HDR or Dolby Vision configuration.");
        }
    }

    internal static void ValidateBlurayOutput(BlurayPlaylist playlist, BluraySelection selection, string json)
    {
        var tracks = BlurayInspector.ParseTracks(json);
        var wanted = new[] { selection.VideoTrackId }.Concat(selection.Audio.Select(t => t.Id)).Concat(selection.Subtitles.Select(t => t.Id))
            .Select(id => playlist.Tracks.Single(t => t.Id == id)).ToList();
        if (tracks.Count != wanted.Count) throw new ArgumentException("The output track count does not match the selection.");
        foreach (var type in new[] { "video", "audio", "subtitles" })
        {
            var source = wanted.Where(t => t.Type == type).OrderBy(t => t.Id).ToArray();
            var result = tracks.Where(t => t.Type == type).OrderBy(t => t.Id).ToArray();
            if (source.Length != result.Length) throw new ArgumentException("A selected track is missing from the output.");
            for (var i = 0; i < source.Length; i++)
                if (source[i].Codec != result[i].Codec || source[i].PixelDimensions != result[i].PixelDimensions ||
                    source[i].Channels != result[i].Channels)
                    throw new ArgumentException("The output did not preserve the selected track's format.");
            for (var i = 0; i < source.Length; i++)
            {
                var requested = selection.Audio.Concat(selection.Subtitles).SingleOrDefault(t => t.Id == source[i].Id);
                if (requested is not null && (requested.Default != result[i].Default || requested.Forced != result[i].Forced ||
                    (requested.Title is not null && requested.Title != (result[i].Title ?? ""))))
                    throw new ArgumentException("The output did not preserve the selected track flags or title.");
            }
        }
        using var document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
        var duration = document.RootElement.GetProperty("container").GetProperty("properties").GetProperty("duration").GetInt64() / 1e9;
        if (Math.Abs(duration - playlist.DurationSeconds) > 1)
            throw new ArgumentException("The MKV duration differs from the selected playlist.");
        var chapters = document.RootElement.TryGetProperty("chapters", out var entries)
            ? entries.EnumerateArray().Sum(c => c.GetProperty("num_entries").GetInt32()) : 0;
        if (chapters != playlist.Chapters) throw new ArgumentException($"The output did not preserve the playlist chapters: expected {playlist.Chapters}, found {chapters}.");
    }
}
