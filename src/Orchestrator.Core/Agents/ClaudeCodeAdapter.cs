using System.Text.Json;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>
/// Claude Code headless mode: <c>claude -p --output-format stream-json --verbose</c>, prompt on stdin,
/// <c>--resume &lt;session_id&gt;</c> for later turns. Uses whatever credentials the CLI already has
/// (subscription login, <c>CLAUDE_CODE_OAUTH_TOKEN</c> or <c>ANTHROPIC_API_KEY</c>).
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    public string Name => "claude";
    public string DefaultExecutable => "claude";
    public string InstallHint => "npm i -g @anthropic-ai/claude-code, then run `claude` and /login";

    public AgentInvocation BuildInvocation(AgentTurnContext context)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        var model = context.Task.Model ?? context.Options.Model;
        if (model is not null) args.AddRange(["--model", model]);
        if (context.ResumeSessionId is not null) args.AddRange(["--resume", context.ResumeSessionId]);
        args.AddRange(context.Options.ExtraArgs ?? []);

        return new AgentInvocation
        {
            Executable = context.Options.Executable ?? DefaultExecutable,
            Arguments = args,
            StandardInput = context.Message,
            Environment = context.Options.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
        };
    }

    public IAgentOutputParser CreateParser() => new Parser();

    public string? GetWatchCommand(AgentTask task, AgentOptions options) =>
        task.SessionId is null || task.WorktreePath is null
            ? null
            : $"cd \"{task.WorktreePath}\" && claude --resume {task.SessionId}";

    /// <summary>Parses <c>stream-json</c> lines: system/init, assistant, user (tool results), result.</summary>
    internal sealed class Parser : IAgentOutputParser
    {
        public string? SessionId { get; private set; }
        public string? FinalMessage { get; private set; }
        public string? Error { get; private set; }
        public string? Model { get; private set; }

        public IEnumerable<ParsedEvent> ParseLine(string line)
        {
            if (!JsonLine.TryParse(line, out var root))
            {
                if (!string.IsNullOrWhiteSpace(line)) yield return new ParsedEvent(AgentEventKind.Log, line);
                yield break;
            }

            SessionId = root.Str("session_id") ?? SessionId;

            // The init frame names the configured model (Qoder may say "auto"); assistant messages name the model that
            // actually answered, which is more precise. Synthetic messages (local errors) are not a model.
            var reported = root.Str("type") switch
            {
                "system" when root.Str("subtype") == "init" => root.Str("model"),
                "assistant" when root.Str("parent_tool_use_id") is null => root.Obj("message")?.Str("model"),
                _ => null,
            };
            if (reported is { Length: > 0 } && !reported.StartsWith('<')
                && (Model is null || (Model == "auto" && reported != "auto") || root.Str("type") == "assistant"))
            {
                Model = reported;
            }
            var isSubagent = root.Str("parent_tool_use_id") is not null;

            switch (root.Str("type"))
            {
                case "system" when root.Str("subtype") == "init":
                    // Claude Code puts session_id in the init frame; Qoder only in later frames.
                    yield return new ParsedEvent(AgentEventKind.Log,
                        SessionId is null ? $"model {root.Str("model")}" : $"session {SessionId} · model {root.Str("model")}");
                    break;

                case "assistant":
                    foreach (var block in Content(root))
                    {
                        switch (block.Str("type"))
                        {
                            case "text" when block.Str("text") is { Length: > 0 } text:
                                if (!isSubagent) FinalMessage = text;
                                yield return new ParsedEvent(AgentEventKind.Message, isSubagent ? $"[subagent] {text}" : text);
                                break;
                            case "thinking" when block.Str("thinking") is { Length: > 0 } thinking:
                                yield return new ParsedEvent(AgentEventKind.Reasoning, thinking);
                                break;
                            case "tool_use":
                                yield return ToolUse(block.Str("name") ?? "tool", block.Obj("input"));
                                break;
                        }
                    }
                    break;

                case "user":
                    foreach (var block in Content(root))
                    {
                        if (block.Str("type") == "tool_result" && block.Bool("is_error"))
                        {
                            yield return new ParsedEvent(AgentEventKind.Error, "tool error",
                                JsonLine.Compact(block.TryGetProperty("content", out var c) ? c : null, 2000));
                        }
                    }
                    break;

                case "result":
                    if (root.Str("result") is { Length: > 0 } result) FinalMessage = result;
                    if (root.Bool("is_error"))
                    {
                        Error = root.Str("result") ?? root.Str("subtype") ?? "Claude Code reported an error";
                        yield return new ParsedEvent(AgentEventKind.Error, Error);
                    }
                    break;
            }
        }

        private static IEnumerable<JsonElement> Content(JsonElement root) =>
            root.Obj("message")?.Obj("content") is { ValueKind: JsonValueKind.Array } content
                ? content.EnumerateArray()
                : [];

        private static ParsedEvent ToolUse(string name, JsonElement? input)
        {
            var i = input ?? default;
            return name switch
            {
                "Bash" => new ParsedEvent(AgentEventKind.Command, i.Str("command") ?? "bash", i.Str("description")),
                "Edit" or "Write" or "MultiEdit" or "NotebookEdit" =>
                    new ParsedEvent(AgentEventKind.FileChange, $"{name} {i.Str("file_path") ?? i.Str("notebook_path")}"),
                _ => new ParsedEvent(AgentEventKind.ToolCall, name, JsonLine.Compact(input)),
            };
        }
    }
}
