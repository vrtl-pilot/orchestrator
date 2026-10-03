using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>
/// OpenCode non-interactive mode: <c>opencode run --format json</c> with the prompt on stdin,
/// <c>--session &lt;id&gt;</c> for later turns. When <see cref="AgentOptions.AttachUrl"/> points at a running
/// <c>opencode serve</c>, the run happens inside that server (<c>--attach</c>), so a human can open the live TUI
/// on the same session with <c>opencode attach &lt;url&gt; --session &lt;id&gt;</c>.
/// </summary>
public sealed class OpenCodeAdapter : IAgentAdapter
{
    public string Name => "opencode";
    public string DefaultExecutable => "opencode";
    public string InstallHint => "npm i -g opencode-ai, then `opencode auth login`";

    public AgentInvocation BuildInvocation(AgentTurnContext context)
    {
        var args = new List<string> { "run", "--format", "json", "--title", $"orchestrator {context.Task.Id}" };
        if (context.Options.AttachUrl is { } url)
        {
            // With --attach, --dir names the directory on the server side; the worktree path is shared on one machine.
            args.AddRange(["--attach", url, "--dir", context.Task.WorktreePath!]);
        }
        var model = context.Task.Model ?? context.Options.Model;
        if (model is not null) args.AddRange(["-m", model]);
        if (context.ResumeSessionId is not null) args.AddRange(["--session", context.ResumeSessionId]);
        args.AddRange(context.Options.ExtraArgs ?? []);

        var env = context.Options.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        env.TryAdd("OPENCODE_DISABLE_AUTOUPDATE", "1");

        return new AgentInvocation
        {
            Executable = context.Options.Executable ?? DefaultExecutable,
            Arguments = args,
            StandardInput = context.Message,
            Environment = env,
        };
    }

    public IAgentOutputParser CreateParser() => new Parser();

    public string? GetWatchCommand(AgentTask task, AgentOptions options)
    {
        if (task.SessionId is null) return null;
        if (options.AttachUrl is { } url) return $"opencode attach {url} --session {task.SessionId}";
        return task.WorktreePath is null ? null : $"cd \"{task.WorktreePath}\" && opencode --session {task.SessionId}";
    }

    /// <summary>Parses <c>opencode run --format json</c> lines: text, reasoning, tool_use, step_*, error.</summary>
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

            SessionId = root.Str("sessionID") ?? SessionId;
            var part = root.Obj("part");

            switch (root.Str("type"))
            {
                case "text" when part?.Str("text") is { Length: > 0 } text:
                    FinalMessage = text;
                    yield return new ParsedEvent(AgentEventKind.Message, text);
                    break;

                case "reasoning" when part?.Str("text") is { Length: > 0 } text:
                    yield return new ParsedEvent(AgentEventKind.Reasoning, text);
                    break;

                case "tool_use" when part is { } p:
                    yield return ToolUse(p);
                    break;

                case "error":
                    var err = root.Obj("error");
                    Error = err?.Obj("data")?.Str("message") ?? err?.Str("name") ?? "error";
                    yield return new ParsedEvent(AgentEventKind.Error, Error);
                    break;
            }
        }
        /// <summary>
        /// Maps a tool part to an event. Tool ids and input keys differ between versions and platforms
        /// (e.g. the shell tool is <c>bash</c> on Unix and <c>shell</c> on Windows), so look for them tolerantly.
        /// </summary>
        private static ParsedEvent ToolUse(System.Text.Json.JsonElement p)
        {
            var tool = p.Str("tool") ?? "tool";
            var state = p.Obj("state");
            var input = state?.Obj("input");
            var title = state?.Str("title");
            var output = state?.Str("output");

            if (state?.Str("status") == "error")
            {
                return new ParsedEvent(AgentEventKind.Error, $"{tool} failed", state?.Str("error"));
            }

            var detail = JsonLine.Truncate(output ?? JsonLine.Compact(input), 2000);
            switch (tool.ToLowerInvariant())
            {
                case "bash" or "shell" or "powershell" or "pwsh":
                    return new ParsedEvent(AgentEventKind.Command, input?.Str("command") ?? NonTrivial(title, tool) ?? tool, detail);

                case "edit" or "write" or "patch" or "multiedit" or "apply_patch":
                    var path = input?.Str("filePath") ?? input?.Str("file_path") ?? input?.Str("path")
                               ?? NonTrivial(title, tool) ?? FirstLine(output);
                    return new ParsedEvent(AgentEventKind.FileChange, path is null ? tool : $"{tool} {path}", detail);

                default:
                    return new ParsedEvent(AgentEventKind.ToolCall, NonTrivial(title, tool) is { } t ? $"{tool}: {t}" : tool, detail);
            }
        }

        /// <summary>The title, unless it merely repeats the tool name.</summary>
        private static string? NonTrivial(string? title, string tool) =>
            string.IsNullOrWhiteSpace(title) || title.Equals(tool, StringComparison.OrdinalIgnoreCase) ? null : title;

        private static string? FirstLine(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : JsonLine.Truncate(text.Trim().Split('\n')[0].Trim(), 160);
    }
}
