using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Orchestrator.Core.Execution;

/// <summary>Validation or state errors that should be reported to the caller rather than logged as failures.</summary>
public class OrchestratorException(string message) : Exception(message);

public sealed class TaskNotFoundException(string taskId) : OrchestratorException($"Task '{taskId}' not found.");

/// <summary>Shared in-process coordination between <see cref="TaskService"/> and <see cref="TaskRunner"/>.</summary>
public sealed class TaskCoordination
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _changes = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public Channel<string> Queue { get; } = Channel.CreateUnbounded<string>();

    /// <summary>Serializes read-modify-write of one task between the API and the runner.</summary>
    public async Task<IDisposable> LockAsync(string taskId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <summary>Wakes everyone waiting on this task (long-poll waits, SSE status, ...).</summary>
    public void NotifyChanged(string taskId)
    {
        if (_changes.TryRemove(taskId, out var tcs)) tcs.TrySetResult();
    }

    /// <summary>Completes when the task next changes, or after <paramref name="timeout"/>.</summary>
    public async Task WaitForChangeAsync(string taskId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _changes.GetOrAdd(taskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
    }

    public CancellationToken RegisterRunning(string taskId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[taskId] = cts;
        return cts.Token;
    }

    public void UnregisterRunning(string taskId)
    {
        if (_running.TryRemove(taskId, out var cts)) cts.Dispose();
    }

    public bool TryCancelRunning(string taskId)
    {
        if (!_running.TryGetValue(taskId, out var cts)) return false;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
