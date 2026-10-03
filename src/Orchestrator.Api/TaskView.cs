using Orchestrator.Core.Model;

namespace Orchestrator.Api;

/// <summary>
/// What callers (MCP tools, HTTP, the CLI) get back for a task. Large fields are capped so a result always
/// fits comfortably in an agent's context; the full diff is available via <see cref="PatchPath"/> or git.
/// </summary>
public sealed record TaskView
{
    public required string Id { get; init; }
    public required string Agent { get; init; }

    /// <summary>Model requested for the task (null = the agent's default).</summary>
    public string? Model { get; init; }

    /// <summary>Model the agent actually used (reported by the agent, or its configured default).</summary>
    public string? ModelUsed { get; init; }
    public required AgentTaskStatus Status { get; init; }
    public required string NextStep { get; init; }

    /// <summary>True when the caller must look before merging: tests failed, or the task failed / needs input.</summary>
    public bool NeedsAttention { get; init; }

    public string? Notice { get; init; }
    public string? PendingQuestion { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public string? RepoRoot { get; init; }
    public string? Branch { get; init; }
    public string? WorktreePath { get; init; }
    public string? BaseSha { get; init; }
    public bool BaseIncludesUncommitted { get; init; }
    public string? HeadSha { get; init; }
    public string? DiffStat { get; init; }
    public int ChangedFileCount { get; init; }
    public IReadOnlyList<ChangedFile>? ChangedFiles { get; init; }
    public string? PatchPath { get; init; }
    public bool? TestsPassed { get; init; }
    public int? TestExitCode { get; init; }
    public string? TestOutputTail { get; init; }
    public string? WatchCommand { get; init; }
    public string? DashboardUrl { get; init; }
    public string? ParentTaskId { get; init; }
    public int Turns { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public static TaskView From(AgentTask t, string? publicUrl, bool compact = false) => new()
    {
        Id = t.Id,
        Agent = t.Agent,
        Model = t.Model,
        ModelUsed = t.ModelUsed,
        Status = t.Status,
        NextStep = NextStepFor(t),
        NeedsAttention = t.Status is AgentTaskStatus.InputRequired or AgentTaskStatus.Failed
                         || (t.Status == AgentTaskStatus.Completed && t.TestsPassed == false),
        Notice = t.Notice,
        PendingQuestion = t.PendingQuestion,
        Summary = Cap(t.Summary, compact ? 600 : 6000),
        Error = Cap(t.Error, 2000),
        RepoRoot = compact ? null : t.RepoRoot,
        Branch = t.Branch,
        WorktreePath = compact ? null : t.WorktreePath,
        BaseSha = compact ? null : t.BaseSha,
        BaseIncludesUncommitted = t.BaseIncludesUncommitted,
        HeadSha = compact ? null : t.HeadSha,
        DiffStat = compact ? null : Cap(t.DiffStat, 3000),
        ChangedFileCount = t.ChangedFiles.Count,
        ChangedFiles = compact ? null : t.ChangedFiles.Take(100).ToList(),
        PatchPath = compact ? null : t.PatchPath,
        TestsPassed = t.TestsPassed,
        TestExitCode = compact ? null : t.TestExitCode,
        TestOutputTail = compact ? null : Cap(t.TestOutputTail, 3000, fromEnd: true),
        WatchCommand = t.WatchCommand,
        DashboardUrl = publicUrl is null ? null : $"{publicUrl.TrimEnd('/')}/#task={t.Id}",
        ParentTaskId = t.ParentTaskId,
        Turns = t.Turns,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        CompletedAt = t.CompletedAt,
    };

    private static string NextStepFor(AgentTask t) => t.Status switch
    {
        AgentTaskStatus.Queued or AgentTaskStatus.Preparing or AgentTaskStatus.Running or AgentTaskStatus.Testing =>
            "Still working. Call wait_task again, or continue with other work and check back later. get_task_events shows live progress.",
        AgentTaskStatus.InputRequired =>
            "The delegated agent is blocked on the question in pendingQuestion. Decide (ask the user if it is their call) and send the decision with answer_task; the agent then resumes in the same session.",
        AgentTaskStatus.Completed when t.TestsPassed == false =>
            $"Tests FAILED (exit {t.TestExitCode}). Do not merge yet. Read testOutputTail: if the code is wrong, send the failure "
            + "back with continue_task; if the test command itself is wrong (it runs in this machine's shell: Git Bash on Windows "
            + "when installed), fix it and delegate again. The agent's own claim that tests pass is not sufficient.",
        AgentTaskStatus.Completed => Integrate(t),
        AgentTaskStatus.Failed =>
            "Read error and summary. Partial work (if any) is committed on the branch. continue_task resumes the agent session with new instructions; otherwise cleanup_task.",
        AgentTaskStatus.Cancelled => "Cancelled. Partial work (if any) is on the branch; call cleanup_task to remove it.",
        _ => string.Empty,
    };

    private static string Integrate(AgentTask t)
    {
        var review = $"Review the result (summary, diffStat, tests; full diff: `git diff {Short(t.BaseSha)} {t.Branch}` or the patch file). ";
        var integrate = t.BaseIncludesUncommitted
            // The base is a snapshot of the caller's uncommitted work; that commit is not in the caller's history,
            // so `git merge` would conflict with the caller's own copy of those edits. Apply only the agent's delta.
            ? $"Your uncommitted changes were part of this task's starting point, so do NOT git merge the branch. Apply only the agent's changes "
              + $"onto your working tree with `git apply \"{t.PatchPath}\"` (if that fails because files changed since, commit your work and use "
              + $"`git apply --3way \"{t.PatchPath}\"`). "
            : $"Integrate it into your working tree with `git merge --no-ff {t.Branch}` (or `git apply \"{t.PatchPath}\"`). ";
        return review + integrate
            + "Then call cleanup_task with delete_branch=true (it refuses to delete the branch unless its changes are in your tree, unless force=true). "
            + "Use continue_task for follow-up changes in the same worktree.";
    }

    private static string Short(string? sha) => sha is null ? "<base>" : sha[..Math.Min(12, sha.Length)];

    private static string? Cap(string? text, int max, bool fromEnd = false)
    {
        if (text is null || text.Length <= max) return text;
        return fromEnd ? "…" + text[^max..] : text[..max] + "…";
    }
}
