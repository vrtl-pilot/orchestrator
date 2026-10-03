namespace Orchestrator.Core.Model;

public enum AgentEventKind
{
    /// <summary>Task lifecycle change; <see cref="AgentEvent.Text"/> is the new status name.</summary>
    Status,
    /// <summary>Assistant text.</summary>
    Message,
    Reasoning,
    /// <summary>A tool invocation (file read/edit, search, MCP tool, ...).</summary>
    ToolCall,
    /// <summary>A shell command the agent ran.</summary>
    Command,
    FileChange,
    Error,
    /// <summary>Orchestrator-side information (worktree created, git output, stderr lines, ...).</summary>
    Log,
    /// <summary>Output of the post-run test command.</summary>
    Test,
}

/// <summary>
/// One normalized, agent-independent progress event. Every adapter's raw output is mapped to these,
/// so the dashboard, <c>orch watch</c> and MCP callers see the same live view for every agent.
/// </summary>
public sealed record AgentEvent(
    long Seq,
    DateTimeOffset Timestamp,
    string TaskId,
    AgentEventKind Kind,
    string Text,
    string? Detail = null);
