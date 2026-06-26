using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Realtime;

/// <summary>
/// Bridges engine events and a periodic progress tick onto the <see cref="TranscodeEventStream"/>: live
/// progress for every job every 1.5s, plus started/completed/errored transitions.
/// </summary>
public sealed class TranscodeProgressBroadcaster(
    ITranscodeEngine engine,
    TranscodeEventStream stream,
    ILogger<TranscodeProgressBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(1500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.JobStarted += OnJobStarted;
        engine.JobCompleted += OnJobCompleted;
        engine.JobFailed += OnJobFailed;

        try
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var snapshot in engine.GetAllSnapshots())
                    {
                        stream.Publish(new TranscodeEvent("progress", snapshot.JobId, snapshot));
                    }
                }
                catch (Exception exception)
                {
                    // A transient engine error must not kill the broadcast loop forever.
                    logger.LogError(exception, "Error broadcasting periodic transcode progress.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            engine.JobStarted -= OnJobStarted;
            engine.JobCompleted -= OnJobCompleted;
            engine.JobFailed -= OnJobFailed;
        }
    }

    private void OnJobStarted(object? sender, string jobId) => Publish("started", jobId);

    private void OnJobCompleted(object? sender, string jobId) => Publish("completed", jobId);

    private void OnJobFailed(object? sender, string jobId) => Publish("errored", jobId);

    private void Publish(string type, string jobId)
    {
        try
        {
            stream.Publish(new TranscodeEvent(type, jobId, engine.GetSnapshot(jobId)));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish {Type} event for {JobId}.", type, jobId);
        }
    }
}
