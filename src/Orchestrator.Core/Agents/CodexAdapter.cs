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
    public string DisplayName => "Codex";
    // Network inside the sandbox so package restores (dotnet restore, npm install) work; writes stay limited to the worktree.
    public IReadOnlyList<string> DefaultExtraArgs => ["--sandbox", "workspace-write", "-c", "sandbox_workspace_write.network_access=true"];
    public string InstallHint => "npm i -g @openai/codex, then `codex login`";

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

    /// <summary>The model Codex uses when none is requested: top-level <c>model</c> in <c>$CODEX_HOME/config.toml</c>.</summary>
    public Task<string?> ResolveModelAsync(AgentTask task, AgentOptions options, CancellationToken ct)
    {
        var home = options.Environment.GetValueOrDefault("CODEX_HOME")
                   ?? Environment.GetEnvironmentVariable("CODEX_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var model = ReadTopLevelTomlString(Path.Combine(home, "config.toml"), "model");
        return Task.FromResult<string?>(model is null ? "codex default" : $"{model} (config.toml default)");
    }

    /// <summary>Reads <c>key = "value"</c> before the first <c>[table]</c> header. Enough for Codex's flat defaults.</summary>
    internal static string? ReadTopLevelTomlString(string path, string key)
    {
        if (!File.Exists(path)) return null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) break;
            if (line.StartsWith('#') || !line.StartsWith(key, StringComparison.Ordinal)) continue;
            var rest = line[key.Length..].TrimStart();
            if (!rest.StartsWith('=')) continue;
            var value = rest[1..].Trim();
            var hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) value = value[..hash].Trim();
            return value.Trim('"', '\'').Trim() is { Length: > 0 } v ? v : null;
        }
        return null;
    }

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

        /// <summary><c>codex exec --json</c> does not name its model, except when it reroutes to another one.</summary>
        public string? Model { get; private set; }

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
                case "error" when item.Str("message") is { } m && m.StartsWith("model rerouted:", StringComparison.Ordinal):
                    // "model rerouted: <from> -> <to> (<reason>)"
                    var to = m["model rerouted:".Length..].Split("->", 2) is [_, var rest] ? rest.Trim().Split(' ')[0] : null;
                    if (to is { Length: > 0 }) Model = to;
                    return new ParsedEvent(AgentEventKind.Log, m);
                case "error":
                    return new ParsedEvent(AgentEventKind.Error, item.Str("message") ?? "error");
                default:
                    return null;
            }
        }
    }
}
