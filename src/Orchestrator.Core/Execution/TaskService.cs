using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Events;
using Orchestrator.Core.Git;
using Orchestrator.Core.Model;
using Orchestrator.Core.Storage;

namespace Orchestrator.Core.Execution;

/// <summary>
/// The orchestrator's public operations. HTTP endpoints, MCP tools and (through HTTP) the <c>orch</c> CLI are
/// thin wrappers over this class, so every front door behaves identically.
/// </summary>
public sealed class TaskService(
    ITaskStore store,
    TaskEventLog events,
    TaskCoordination coordination,
    AgentRegistry agents,
    WorktreeManager worktrees,
    IOptions<OrchestratorOptions> options)
{
    private readonly OrchestratorOptions _options = options.Value;

    public async Task<AgentTask> DelegateAsync(DelegateRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new OrchestratorException("prompt is required.");
        if (!agents.TryGet(request.Agent, out var adapter, out _))
        {
            throw new OrchestratorException(
                $"Unknown or disabled agent '{request.Agent}'. Available: {string.Join(", ", agents.Describe().Where(a => a.Enabled).Select(a => a.Name))}.");
        }

        // Fail before creating a worktree if the CLI cannot be started at all.
        var info = agents.Describe().First(a => a.Name.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase));
        if (info.ResolvedPath is null)
        {
            throw new OrchestratorException(
                $"The {adapter.Name} CLI ('{info.Executable}') was not found on the orchestrator's PATH. "
                + (adapter.InstallHint is { } hint ? $"Install it ({hint}), " : "Install it, ")
                + $"or set Orchestrator:Agents:{adapter.Name}:Executable to its full path (on Windows, the .cmd or .exe file). "
                + "Then restart the orchestrator so it picks up the new PATH.");
        }

        if (request.ClientRequestId is { Length: > 0 } clientId
            && await store.FindByClientRequestIdAsync(clientId, ct) is { } existing)
        {
            return existing;
        }

        var depth = 1;
        if (request.ParentTaskId is { Length: > 0 } parentId)
        {
            var parent = await store.GetAsync(parentId, ct)
                ?? throw new OrchestratorException($"Parent task '{parentId}' not found.");
            depth = parent.Depth + 1;
            if (depth > _options.MaxDepth)
            {
                throw new OrchestratorException(
                    $"Delegation depth {depth} exceeds the configured maximum of {_options.MaxDepth}. Do this work yourself instead of delegating further.");
            }
        }

        var repoRoot = await ResolveRepoRootAsync(request.RepoPath, ct);

        var task = new AgentTask
        {
            Id = NewId(),
            Agent = request.Agent.ToLowerInvariant(),
            Prompt = request.Prompt,
            RepoRoot = repoRoot,
            BaseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? null : request.BaseRef,
            IncludeUncommitted = request.IncludeUncommitted,
            Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
            TestCommand = string.IsNullOrWhiteSpace(request.TestCommand) ? null : request.TestCommand,
            TimeoutMinutes = request.TimeoutMinutes,
            ParentTaskId = string.IsNullOrWhiteSpace(request.ParentTaskId) ? null : request.ParentTaskId,
            Depth = depth,
            ClientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId,
        };

        await store.SaveAsync(task, ct);
        events.Append(task.Id, AgentEventKind.Status, nameof(AgentTaskStatus.Queued), $"{task.Agent} · {repoRoot}");
        await coordination.Queue.Writer.WriteAsync(task.Id, ct);
        return task;
    }

    public Task<AgentTask?> GetAsync(string id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public Task<IReadOnlyList<AgentTask>> ListAsync(AgentTaskStatus? status = null, string? parentTaskId = null, int limit = 50, CancellationToken ct = default) =>
        store.ListAsync(status, parentTaskId, limit, ct);

    /// <summary>
    /// Long-poll: returns as soon as the task is settled (completed, failed, cancelled or input-required)
    /// or when <paramref name="maxWait"/> elapses. Callers loop; no single call blocks for long, which keeps
    /// MCP tool calls inside client idle timeouts.
    /// </summary>
    public async Task<AgentTask> WaitAsync(string id, TimeSpan maxWait, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + maxWait;
        while (true)
        {
            var task = await RequireAsync(id, ct);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (task.Status.IsSettled() || remaining <= TimeSpan.Zero) return task;
            await coordination.WaitForChangeAsync(id, remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Answers the question of a task in <see cref="AgentTaskStatus.InputRequired"/> and resumes the agent session.</summary>
    public async Task<AgentTask> AnswerAsync(string id, string answer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(answer)) throw new OrchestratorException("answer is required.");
        using var _ = await coordination.LockAsync(id, ct);
        var task = await RequireAsync(id, ct);
        if (task.Status != AgentTaskStatus.InputRequired)
        {
            throw new OrchestratorException($"Task {id} is {task.Status}, not input_required. Use continue_task for follow-ups.");
        }
        return await RequeueAsync(task, TaskBrief.Answer(answer, _options.InputRequestMarker), $"answer: {answer}", ct);
    }

    /// <summary>Sends a follow-up to a finished task's agent session (same worktree, same conversation).</summary>
    public async Task<AgentTask> ContinueAsync(string id, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new OrchestratorException("message is required.");
        using var _ = await coordination.LockAsync(id, ct);
        var task = await RequireAsync(id, ct);
        if (task.Status == AgentTaskStatus.InputRequired)
        {
            return await RequeueAsync(task, TaskBrief.Answer(message, _options.InputRequestMarker), $"answer: {message}", ct);
        }
        if (task.Status is not (AgentTaskStatus.Completed or AgentTaskStatus.Failed) || task.SessionId is null
            || task.WorktreePath is null || !Directory.Exists(task.WorktreePath))
        {
            throw new OrchestratorException(
                $"Task {id} cannot be continued (status {task.Status}{(task.SessionId is null ? ", no agent session" : "")}{(task.WorktreePath is null || !Directory.Exists(task.WorktreePath) ? ", worktree removed" : "")}).");
        }
        return await RequeueAsync(task, TaskBrief.FollowUp(message, _options.InputRequestMarker), $"follow-up: {message}", ct);
    }

    public async Task<AgentTask> CancelAsync(string id, CancellationToken ct = default)
    {
        using var _ = await coordination.LockAsync(id, ct);
        var task = await RequireAsync(id, ct);
        if (task.Status.IsTerminal()) return task;

        if (coordination.TryCancelRunning(id))
        {
            events.Append(id, AgentEventKind.Log, "cancellation requested");
            return task; // The runner records the final Cancelled state once the process tree is gone.
        }

        task.Status = AgentTaskStatus.Cancelled;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.NextMessage = null;
        await store.SaveAsync(task, ct);
        events.Append(id, AgentEventKind.Status, nameof(AgentTaskStatus.Cancelled));
        coordination.NotifyChanged(id);
        return task;
    }

    /// <summary>
    /// Removes the task's worktree once the caller has integrated or discarded it. With <paramref name="deleteBranch"/>
    /// the branch is deleted only if it is merged into the repository's current HEAD, unless <paramref name="force"/>.
    /// </summary>
    public async Task<AgentTask> CleanupAsync(string id, bool deleteBranch, bool force = false, CancellationToken ct = default)
    {
        using var _ = await coordination.LockAsync(id, ct);
        var task = await RequireAsync(id, ct);
        if (!task.Status.IsSettled())
        {
            throw new OrchestratorException($"Task {id} is {task.Status}; cancel it before cleaning up.");
        }

        var notes = new List<string>();
        if (task.WorktreePath is not null)
        {
            await worktrees.RemoveAsync(task.RepoRoot, task.WorktreePath, task.Branch, deleteBranch: false, ct);
            await GitClient.RunAsync(task.RepoRoot, ["update-ref", "-d", $"refs/orchestrator/base/{task.Id}"], cancellationToken: ct);
            task.WorktreePath = null;
            notes.Add("worktree removed");
        }

        if (deleteBranch && task.Branch is not null)
        {
            var result = await worktrees.DeleteBranchAsync(task.RepoRoot, task.Branch, force, task.PatchPath, ct);
            if (result.Deleted)
            {
                notes.Add(result.Merged ? $"branch {task.Branch} deleted (integrated)" : $"branch {task.Branch} deleted (unmerged work discarded, force)");
                task.Branch = null;
            }
            else
            {
                notes.Add($"branch {task.Branch} kept: its changes are not in {result.CurrentBranch ?? "HEAD"} (not merged, patch not applied). "
                          + "Integrate it first (see nextStep), or clean up with force=true to discard the work");
            }
        }
        else if (task.Branch is not null)
        {
            notes.Add($"branch {task.Branch} kept");
        }

        task.Notice = notes.Count == 0 ? "nothing to clean up" : string.Join("; ", notes) + ".";
        events.Append(id, AgentEventKind.Log, task.Notice);
        await store.SaveAsync(task, ct);
        coordination.NotifyChanged(id);
        return task;
    }

    public IReadOnlyList<AgentEvent> GetEvents(string id, long afterSeq = 0, int limit = 200) =>
        events.Read(id, afterSeq, limit);

    public IAsyncEnumerable<AgentEvent> FollowEventsAsync(string id, long afterSeq, CancellationToken ct) =>
        events.FollowAsync(id, afterSeq, ct);

    public IReadOnlyList<AgentInfo> DescribeAgents() => agents.Describe();

    private async Task<AgentTask> RequeueAsync(AgentTask task, string message, string logText, CancellationToken ct)
    {
        task.NextMessage = message;
        task.PendingQuestion = null;
        task.Notice = null;
        task.Error = null;
        task.Status = AgentTaskStatus.Queued;
        task.CompletedAt = null;
        await store.SaveAsync(task, ct);
        events.Append(task.Id, AgentEventKind.Log, logText);
        events.Append(task.Id, AgentEventKind.Status, nameof(AgentTaskStatus.Queued));
        coordination.NotifyChanged(task.Id);
        await coordination.Queue.Writer.WriteAsync(task.Id, ct);
        return task;
    }

    private async Task<AgentTask> RequireAsync(string id, CancellationToken ct) =>
        await store.GetAsync(id, ct) ?? throw new TaskNotFoundException(id);

    private async Task<string> ResolveRepoRootAsync(string? repoPath, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(repoPath) ? _options.DefaultRepository : repoPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new OrchestratorException("repo_path is required (no default repository is configured).");
        }

        string root;
        try
        {
            root = await WorktreeManager.GetRepoRootAsync(Path.GetFullPath(path), ct);
        }
        catch (GitException ex)
        {
            throw new OrchestratorException($"'{path}' is not inside a Git repository: {ex.Message}");
        }

        if (_options.AllowedRepositoryRoots.Count > 0
            && !_options.AllowedRepositoryRoots.Any(allowed => IsUnder(root, Path.GetFullPath(allowed))))
        {
            throw new OrchestratorException($"Repository '{root}' is outside the allowed repository roots.");
        }

        if (!(await GitClient.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken: ct)).Ok)
        {
            throw new OrchestratorException(
                $"Repository '{root}' has no commits yet, so there is nothing to branch a worktree from. "
                + "Commit something first (git add -A && git commit -m init), then delegate again.");
        }
        return root;
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).StartsWith(normalizedRoot, comparison);
    }

    /// <summary>Short, sortable, URL- and branch-safe id such as <c>t20261003-7f3a9c</c>.</summary>
    private static string NewId() =>
        $"t{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
}
