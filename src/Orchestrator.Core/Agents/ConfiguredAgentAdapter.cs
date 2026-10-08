using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>
/// A platform defined entirely in configuration (Setup page or config.json): no code needed for CLIs that speak one of
/// the supported protocols. Placeholders in argument lists: <c>{model}</c>, <c>{sessionId}</c>, <c>{prompt}</c>.
/// </summary>
public sealed class ConfiguredAgentAdapter(string name, AgentOptions options) : IAgentAdapter
{
    public const string ClaudeStreamJson = "claude-stream-json";
    public const string CodexJsonl = "codex-jsonl";
    public const string OpenCodeJson = "opencode-json";
    public const string Text = "text";

    public static readonly IReadOnlyList<string> Protocols = [ClaudeStreamJson, CodexJsonl, OpenCodeJson, Text];

    public string Name => name;
    public string DisplayName => options.DisplayName ?? name;
    public string DefaultExecutable => options.Executable ?? name;
    public string? InstallHint => options.InstallHint;
    public string Protocol => options.Protocol ?? Text;

    public AgentInvocation BuildInvocation(AgentTurnContext context)
    {
        var o = context.Options;
        var model = context.Task.Model ?? o.Model;
        var canResume = o.ResumeArgs is { Count: > 0 } && context.ResumeSessionId is not null;
        var message = context.Message;

        // Without a resumable session, a follow-up turn would lose the conversation; restate it.
        if (context.Task.Turns > 0 && !canResume)
        {
            message = $"""
                You are continuing an earlier task in this same working directory.
                Original task:
                {context.Task.Prompt}

                Your previous final message:
                {context.Task.Summary ?? "(none)"}

                {context.Message}
                """;
        }

        var promptAsArg = string.Equals(o.PromptVia ?? options.PromptVia, "arg", StringComparison.OrdinalIgnoreCase);
        var baseArgs = o.Args ?? options.Args ?? [];
        var args = new List<string>();
        var promptPlaced = false;
        foreach (var arg in baseArgs)
        {
            if (arg.Contains("{prompt}"))
            {
                if (!promptAsArg) continue; // {prompt} only applies when the prompt goes on the command line.
                args.Add(arg.Replace("{prompt}", message));
                promptPlaced = true;
                continue;
            }
            args.Add(arg);
        }
        if (model is not null) args.AddRange(Expand(o.ModelArgs ?? options.ModelArgs, model, null));
        if (canResume) args.AddRange(Expand(o.ResumeArgs, null, context.ResumeSessionId));
        args.AddRange(o.ExtraArgs ?? []);
        if (promptAsArg && !promptPlaced) args.Add(message);

        return new AgentInvocation
        {
            Executable = o.Executable ?? DefaultExecutable,
            Arguments = args,
            StandardInput = promptAsArg ? null : message,
            Environment = o.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
        };
    }

    public IAgentOutputParser CreateParser() => Protocol switch
    {
        ClaudeStreamJson => new ClaudeCodeAdapter.Parser(),
        CodexJsonl => new CodexAdapter.Parser(),
        OpenCodeJson => new OpenCodeAdapter.Parser(),
        _ => new TextParser(),
    };

    public string? GetWatchCommand(AgentTask task, AgentOptions options) => null;

    private static IEnumerable<string> Expand(IEnumerable<string>? template, string? model, string? sessionId) =>
        (template ?? []).Select(a => a.Replace("{model}", model ?? string.Empty).Replace("{sessionId}", sessionId ?? string.Empty));

    /// <summary>
    /// For CLIs without structured output: every stdout line is shown live, and the whole output is the final message.
    /// </summary>
    internal sealed class TextParser : IAgentOutputParser
    {
        private readonly List<string> _lines = [];

        public string? SessionId => null;
        public string? Error => null;
        public string? FinalMessage => _lines.Count == 0 ? null : string.Join('\n', _lines).Trim();

        public IEnumerable<ParsedEvent> ParseLine(string line)
        {
            _lines.Add(line);
            if (string.IsNullOrWhiteSpace(line)) yield break;
            yield return new ParsedEvent(AgentEventKind.Message, line);
        }
    }
}
