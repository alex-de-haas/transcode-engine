using System.Collections.Concurrent;
using System.Threading.Channels;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Realtime;

/// <summary>One event on the control SSE stream.</summary>
/// <param name="Type"><c>progress</c> | <c>started</c> | <c>completed</c> | <c>errored</c>.</param>
/// <param name="JobId">The job's id.</param>
/// <param name="Snapshot">The job snapshot for this event.</param>
public sealed record TranscodeEvent(string Type, string JobId, JobSnapshot? Snapshot);

/// <summary>
/// Fan-out hub for transcode events: <see cref="TranscodeProgressBroadcaster"/> publishes, each
/// <c>GET /events</c> subscriber drains its own bounded channel (slow readers drop oldest).
/// </summary>
public sealed class TranscodeEventStream
{
    private readonly ConcurrentDictionary<Guid, Channel<TranscodeEvent>> _subscribers = new();

    public (Guid Id, ChannelReader<TranscodeEvent> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<TranscodeEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(TranscodeEvent evt)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }
}
