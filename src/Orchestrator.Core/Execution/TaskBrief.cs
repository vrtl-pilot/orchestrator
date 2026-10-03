using System.Text;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Execution;

/// <summary>
/// Builds the messages sent to delegated agents and recognizes the "needs input" convention.
/// The convention is plain text so it works identically with every agent's one-shot CLI mode:
/// the agent stops and ends its final message with <c>NEEDS_INPUT: question</c>; the orchestrator
/// relays the question, and the answer is sent as the next turn of the same agent session.
/// </summary>
public static class TaskBrief
{
    public static string FirstTurn(AgentTask task, string marker)
    {
        var sb = new StringBuilder();
        sb.AppendLine(task.Prompt.Trim());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Orchestrator context (from the system that delegated this task to you):");
        sb.AppendLine($"- You are working in an isolated Git worktree at `{task.WorktreePath}` on branch `{task.Branch}`. Only modify files inside it.");
        sb.AppendLine("- Do not push, switch branches, rebase, reset, or touch other worktrees. Committing is optional: the orchestrator commits whatever you leave.");
        if (task.TestCommand is { Length: > 0 } test)
        {
            sb.AppendLine($"- After you finish, the orchestrator runs `{test}` in this worktree. Make it pass.");
        }
        sb.AppendLine("- Finish with a concise summary: what you changed, why, and anything the requester must know or verify.");
        sb.AppendLine($"- If you cannot continue without a decision from the requester, stop and make the last line of your final message `{marker} <your question, listing the options you see>`. Do not use it for anything you can reasonably decide yourself.");
        return sb.ToString();
    }

    public static string Answer(string answer, string marker) =>
        $"Answer from the requester: {answer.Trim()}\n\nContinue the task. Same rules as before (end with a summary, or `{marker} <question>` if you are blocked again).";

    public static string FollowUp(string message, string marker) =>
        $"Follow-up request from the requester: {message.Trim()}\n\nSame rules as before (end with a summary, or `{marker} <question>` if you are blocked).";

    /// <summary>Returns the question if the agent's final message asks for input, otherwise null.</summary>
    public static string? ExtractQuestion(string? finalMessage, string marker)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return null;
        var index = finalMessage.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        // Only honour a marker at the start of a line (ignoring markdown decoration), so quoting the
        // convention mid-sentence does not trigger it.
        var lineStart = index == 0 ? 0 : finalMessage.LastIndexOf('\n', index - 1) + 1;
        if (finalMessage[lineStart..index].Trim(' ', '\t', '*', '`', '>', '-', '_').Length > 0) return null;

        var question = finalMessage[(index + marker.Length)..].Trim().Trim('`', '*').Trim();
        return question.Length > 0 ? question : null;
    }
}
