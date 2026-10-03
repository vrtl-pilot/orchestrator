using System.Text.Json;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>
/// Codex CLI non-interactive mode: <c>codex exec --json -o &lt;file&gt; -</c> with the prompt on stdin,
/// <c>codex exec ... resume &lt;thread_id&gt; -</c> for later turns. <c>exec</c> never asks for approval
/// (the sandbox decides), so set the sandbox via <see cref="AgentOptions.ExtraArgs"/>.
/// Uses the CLI's own login (ChatGPT or API key; <c>CODEX_API_KEY</c> also works for exec).
/// </summary>
public sealed class CodexAdapter : IAgentAdapter
{
    public string Name => "codex";
    public string DefaultExecutable => "codex";

    public AgentInvocation BuildInvocation(AgentTurnContext context)
    {
        var lastMessageFile = Path.Combine(context.ScratchDirectory, $"codex-last-message-{context.Task.Turns + 1}.txt");

        // Options must precede the `resume` subcommand; prompt "-" means "read from stdin".
        var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "-o", lastMessageFile };
        var model = context.Task.Model ?? context.Options.Model;
        if (model is not null) args.AddRange(["-m", model]);
        args.AddRange(context.Options.ExtraArgs ?? []);
        if (context.ResumeSessionId is not null) args.AddRange(["resume", context.ResumeSessionId]);
        args.Add("-");

        return new AgentInvocation
        {
            Executable = context.Options.Executable ?? DefaultExecutable,
            Arguments = args,
            StandardInput = context.Message,
            Environment = context.Options.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            LastMessageFile = lastMessageFile,
        };
    }

    public IAgentOutputParser CreateParser() => new Parser();

    public string? GetWatchCommand(AgentTask task, AgentOptions options) =>
        task.SessionId is null || task.WorktreePath is null
            ? null
            : $"cd \"{task.WorktreePath}\" && codex resume {task.SessionId}";

    /// <summary>Parses <c>codex exec --json</c> events (thread.started, item.*, turn.*, error).</summary>
    internal sealed class Parser : IAgentOutputParser
    {
        public string? SessionId { get; private set; }
        public string? FinalMessage { get; private set; }
        public string? Error { get; private set; }

        public IEnumerable<ParsedEvent> ParseLine(string line)
        {
            if (!JsonLine.TryParse(line, out var root))
            {
                if (!string.IsNullOrWhiteSpace(line)) yield return new ParsedEvent(AgentEventKind.Log, line);
                yield break;
            }

            switch (root.Str("type"))
            {
                case "thread.started":
                    SessionId = root.Str("thread_id");
                    yield return new ParsedEvent(AgentEventKind.Log, $"thread {SessionId}");
                    break;

                case "item.started" when root.Obj("item") is { } item && item.Str("type") == "command_execution":
                    yield return new ParsedEvent(AgentEventKind.Command, item.Str("command") ?? "command", "started");
                    break;

                case "item.completed" when root.Obj("item") is { } item:
                    if (Item(item) is { } evt) yield return evt;
                    break;

                case "turn.failed":
                    Error = root.Obj("error")?.Str("message") ?? "turn failed";
                    yield return new ParsedEvent(AgentEventKind.Error, Error);
                    break;

                case "error":
                    Error = root.Str("message") ?? "error";
                    yield return new ParsedEvent(AgentEventKind.Error, Error);
                    break;
            }
        }

        private ParsedEvent? Item(JsonElement item)
        {
            switch (item.Str("type"))
            {
                case "agent_message":
                    FinalMessage = item.Str("text") ?? FinalMessage;
                    return new ParsedEvent(AgentEventKind.Message, item.Str("text") ?? string.Empty);
                case "reasoning":
                    return new ParsedEvent(AgentEventKind.Reasoning, item.Str("text") ?? string.Empty);
                case "command_execution":
                    var exit = item.Int("exit_code");
                    return new ParsedEvent(AgentEventKind.Command, item.Str("command") ?? "command",
                        $"{item.Str("status")} exit={exit?.ToString() ?? "?"}\n{JsonLine.Truncate(item.Str("aggregated_output") ?? string.Empty, 2000)}");
                case "file_change":
                    var changes = item.Obj("changes") is { ValueKind: JsonValueKind.Array } arr
                        ? string.Join(", ", arr.EnumerateArray().Select(c => $"{c.Str("kind")} {c.Str("path")}"))
                        : "files changed";
                    return new ParsedEvent(AgentEventKind.FileChange, changes, item.Str("status"));
                case "mcp_tool_call":
                    return new ParsedEvent(AgentEventKind.ToolCall,
                        $"{item.Str("server")}/{item.Str("tool")}".Trim('/') is { Length: > 0 } n ? n : "mcp tool",
                        item.Str("status"));
                case "web_search":
                    return new ParsedEvent(AgentEventKind.ToolCall, "web_search", item.Str("query"));
                case "todo_list":
                    return new ParsedEvent(AgentEventKind.ToolCall, "todo_list", JsonLine.Compact(item.Obj("items")));
                case "error":
                    return new ParsedEvent(AgentEventKind.Error, item.Str("message") ?? "error");
                default:
                    return null;
            }
        }
    }
}
