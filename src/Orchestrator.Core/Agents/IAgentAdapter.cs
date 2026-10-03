using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>Everything an adapter needs to build one agent turn.</summary>
public sealed record AgentTurnContext
{
    public required AgentTask Task { get; init; }

    /// <summary>Text sent to the agent this turn: the full brief on the first turn, the answer/follow-up afterwards.</summary>
    public required string Message { get; init; }

    /// <summary>Agent session to resume (null on the first turn).</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Scratch directory owned by the orchestrator for this task (outside the worktree).</summary>
    public required string ScratchDirectory { get; init; }

    public required AgentOptions Options { get; init; }
}

/// <summary>A fully described process launch for one agent turn.</summary>
public sealed record AgentInvocation
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? StandardInput { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>File the agent writes its final message to, if it supports that (Codex <c>-o</c>).</summary>
    public string? LastMessageFile { get; init; }
}

/// <summary>A normalized event produced by a parser (sequence numbers are assigned by the event log).</summary>
public sealed record ParsedEvent(AgentEventKind Kind, string Text, string? Detail = null);

/// <summary>Stateful parser for one agent turn's stdout.</summary>
public interface IAgentOutputParser
{
    IEnumerable<ParsedEvent> ParseLine(string line);

    string? SessionId { get; }

    /// <summary>The agent's last assistant message (its answer/summary for the turn).</summary>
    string? FinalMessage { get; }

    /// <summary>An error the agent itself reported (as opposed to a process failure).</summary>
    string? Error { get; }
}

/// <summary>Drives one coding agent CLI. Implementations are stateless; per-turn state lives in the parser.</summary>
public interface IAgentAdapter
{
    /// <summary>Name used in requests and configuration (<c>claude</c>, <c>codex</c>, <c>opencode</c>).</summary>
    string Name { get; }

    string DefaultExecutable { get; }

    AgentInvocation BuildInvocation(AgentTurnContext context);

    IAgentOutputParser CreateParser();

    /// <summary>A command a human can run to see the agent's session (live if the agent supports attaching).</summary>
    string? GetWatchCommand(AgentTask task, AgentOptions options);
}
