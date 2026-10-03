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
                    var tool = p.Str("tool") ?? "tool";
                    var state = p.Obj("state");
                    var input = state?.Obj("input");
                    var status = state?.Str("status");
                    var title = state?.Str("title");
                    var kind = tool switch
                    {
                        "bash" => AgentEventKind.Command,
                        "edit" or "write" or "patch" or "multiedit" => AgentEventKind.FileChange,
                        _ => AgentEventKind.ToolCall,
                    };
                    var text2 = kind switch
                    {
                        AgentEventKind.Command => input?.Str("command") ?? title ?? tool,
                        AgentEventKind.FileChange => $"{tool} {input?.Str("filePath") ?? title}",
                        _ => title is { Length: > 0 } ? $"{tool}: {title}" : tool,
                    };
                    if (status == "error")
                    {
                        yield return new ParsedEvent(AgentEventKind.Error, $"{tool} failed", state?.Str("error"));
                    }
                    else
                    {
                        yield return new ParsedEvent(kind, text2,
                            JsonLine.Truncate(state?.Str("output") ?? JsonLine.Compact(input), 2000));
                    }
                    break;

                case "error":
                    var err = root.Obj("error");
                    Error = err?.Obj("data")?.Str("message") ?? err?.Str("name") ?? "error";
                    yield return new ParsedEvent(AgentEventKind.Error, Error);
                    break;
            }
        }
    }
}
