using System.Diagnostics;

namespace TranscodeEngine.Api.Transcoding;

public sealed partial class FfmpegTranscodeEngine
{
    private async Task<JoinMediaInfo> ProbeJoinAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var psi = new ProcessStartInfo(_settings.FfprobePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-show_chapters",
                     "-show_data_hash", "sha256", "-of", "json", path }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new ArgumentException("Could not start the join inspection.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new ArgumentException($"Cannot inspect a selected part: {await stderr}");
            var result = JoinMediaInfo.Parse(path, await stdout);
            await stderr;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new ArgumentException("Inspecting a selected part timed out; no output was published.");
        }
        catch { TryKillProcess(process); throw; }
    }
}
