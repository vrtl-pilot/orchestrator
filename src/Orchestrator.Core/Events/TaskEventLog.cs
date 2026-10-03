using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Orchestrator.Core.Model;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Events;

/// <summary>
/// Append-only, per-task event log (<c>{data}/events/{taskId}.jsonl</c>) with live fan-out to subscribers.
/// This is what makes a headless agent observable: the dashboard, <c>orch watch</c> and the MCP
/// <c>get_task_events</c> tool all read from here, whichever agent is running.
/// </summary>
public sealed class TaskEventLog
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, TaskStream> _streams = new();

    public TaskEventLog(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "events");
        Directory.CreateDirectory(_directory);
    }

    public AgentEvent Append(string taskId, AgentEventKind kind, string text, string? detail = null)
    {
        text = Ansi.Strip(text);
        detail = Ansi.StripNullable(detail);
        var stream = GetStream(taskId);
        lock (stream)
        {
            var evt = new AgentEvent(++stream.LastSeq, DateTimeOffset.UtcNow, taskId, kind, text, detail);
            File.AppendAllText(PathFor(taskId), JsonSerializer.Serialize(evt, OrchestratorJson.Options) + "\n");
            foreach (var subscriber in stream.Subscribers.Keys)
            {
                subscriber.Writer.TryWrite(evt);
            }
            return evt;
        }
    }

    /// <summary>Events with <see cref="AgentEvent.Seq"/> greater than <paramref name="afterSeq"/>.</summary>
    public IReadOnlyList<AgentEvent> Read(string taskId, long afterSeq = 0, int limit = int.MaxValue)
    {
        var stream = GetStream(taskId);
        lock (stream)
        {
            return ReadFile(taskId).Where(e => e.Seq > afterSeq).Take(limit).ToList();
        }
    }

    /// <summary>Replays stored events after <paramref name="afterSeq"/>, then streams new ones until cancelled.</summary>
    public async IAsyncEnumerable<AgentEvent> FollowAsync(
        string taskId, long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = GetStream(taskId);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        List<AgentEvent> backlog;
        lock (stream)
        {
            // Subscribe and snapshot under the same lock so no event is missed or duplicated.
            stream.Subscribers.TryAdd(channel, 0);
            backlog = ReadFile(taskId).Where(e => e.Seq > afterSeq).ToList();
        }

        try
        {
            var last = afterSeq;
            foreach (var evt in backlog)
            {
                last = evt.Seq;
                yield return evt;
            }
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                if (evt.Seq <= last) continue;
                last = evt.Seq;
                yield return evt;
            }
        }
        finally
        {
            stream.Subscribers.TryRemove(channel, out _);
        }
    }

    private TaskStream GetStream(string taskId) =>
        _streams.GetOrAdd(taskId, id => new TaskStream { LastSeq = ReadFile(id).LastOrDefault()?.Seq ?? 0 });

    private IEnumerable<AgentEvent> ReadFile(string taskId)
    {
        var path = PathFor(taskId);
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            AgentEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AgentEvent>(line, OrchestratorJson.Options);
            }
            catch (JsonException)
            {
                continue; // Torn write after a crash.
            }
            if (evt is not null) yield return evt;
        }
    }

    private string PathFor(string taskId)
    {
        if (taskId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || taskId.Contains(".."))
        {
            throw new ArgumentException("Invalid task id.", nameof(taskId));
        }
        return Path.Combine(_directory, taskId + ".jsonl");
    }

    private sealed class TaskStream
    {
        public long LastSeq;
        public readonly ConcurrentDictionary<Channel<AgentEvent>, byte> Subscribers = new();
    }
}
