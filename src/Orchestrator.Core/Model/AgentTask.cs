namespace Orchestrator.Core.Model;

/// <summary>
/// Lifecycle of a delegated task. Names mirror the A2A task states where they overlap
/// (working ≈ Running, input_required ≈ InputRequired) so an A2A facade can be added later.
/// </summary>
public enum AgentTaskStatus
{
    Queued,
    Preparing,
    Running,
    InputRequired,
    Testing,
    Completed,
    Failed,
    Cancelled,
}

public static class AgentTaskStatusExtensions
{
    public static bool IsTerminal(this AgentTaskStatus status) =>
        status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;

    /// <summary>States in which the caller has to act (or may stop waiting).</summary>
    public static bool IsSettled(this AgentTaskStatus status) =>
        status.IsTerminal() || status == AgentTaskStatus.InputRequired;
}

/// <summary>What a caller (Claude Code, another agent, a human) asks the orchestrator to do.</summary>
public sealed record DelegateRequest
{
    /// <summary>Adapter name: <c>claude</c>, <c>codex</c> or <c>opencode</c>.</summary>
    public required string Agent { get; init; }

    /// <summary>Self-contained brief: goal, constraints, files of interest, acceptance criteria.</summary>
    public required string Prompt { get; init; }

    /// <summary>Any path inside the target Git repository. Falls back to the configured default.</summary>
    public string? RepoPath { get; init; }

    /// <summary>Commit-ish to branch from. Defaults to the caller's HEAD (plus uncommitted work, see below).</summary>
    public string? BaseRef { get; init; }

    /// <summary>When branching from HEAD, include the caller's uncommitted and untracked changes in the base.</summary>
    public bool IncludeUncommitted { get; init; } = true;

    public string? Model { get; init; }

    /// <summary>Shell command run inside the worktree after the agent finishes (e.g. <c>dotnet test</c>).</summary>
    public string? TestCommand { get; init; }

    public int? TimeoutMinutes { get; init; }

    /// <summary>Set when a delegated agent delegates further; used for depth limiting.</summary>
    public string? ParentTaskId { get; init; }

    /// <summary>Idempotency key: re-sending the same key returns the existing task instead of starting a new one.</summary>
    public string? ClientRequestId { get; init; }
}

/// <summary>Persistent state of one delegated task.</summary>
public sealed class AgentTask
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public required string Prompt { get; init; }
    public required string RepoRoot { get; init; }
    public string? BaseRef { get; init; }
    public bool IncludeUncommitted { get; init; } = true;
    public string? Model { get; init; }
    public string? TestCommand { get; init; }
    public int? TimeoutMinutes { get; init; }
    public string? ParentTaskId { get; init; }
    public int Depth { get; init; }
    public string? ClientRequestId { get; init; }

    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Workspace
    public string? Branch { get; set; }
    public string? WorktreePath { get; set; }
    public string? BaseSha { get; set; }

    /// <summary>
    /// The base is a snapshot of the caller's uncommitted work. Integrate with <c>git apply</c> of the patch,
    /// not <c>git merge</c>: the snapshot commit is not in the caller's history and would conflict.
    /// </summary>
    public bool BaseIncludesUncommitted { get; set; }
    public string? HeadSha { get; set; }

    // Agent session (used to resume after input-required or for follow-ups)
    public string? SessionId { get; set; }
    public int Turns { get; set; }

    /// <summary>Message to send on the next turn (an answer or a follow-up). Null on the first turn.</summary>
    public string? NextMessage { get; set; }

    /// <summary>The question the agent asked when <see cref="Status"/> is <see cref="AgentTaskStatus.InputRequired"/>.</summary>
    public string? PendingQuestion { get; set; }

    // Results
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public string? DiffStat { get; set; }
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public string? PatchPath { get; set; }
    public bool? TestsPassed { get; set; }
    public int? TestExitCode { get; set; }
    public string? TestOutputTail { get; set; }

    /// <summary>Informational message from the last management operation (e.g. why a branch was kept on cleanup).</summary>
    public string? Notice { get; set; }

    /// <summary>Command a human can run to look at the agent's session (live where the agent supports it).</summary>
    public string? WatchCommand { get; set; }
}

public sealed record ChangedFile(string Status, string Path);
