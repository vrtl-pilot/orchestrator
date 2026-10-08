using Orchestrator.Core.Model;

namespace Orchestrator.Core.Agents;

/// <summary>
/// Qoder CLI headless mode: <c>qodercli -p --output-format stream-json</c>, prompt on stdin,
/// <c>--resume &lt;session_id&gt;</c> for later turns. Its stream-json protocol uses the same frames as Claude Code
/// (<c>system/init</c>, <c>assistant</c>, <c>user</c>, <c>result</c> with <c>session_id</c>/<c>is_error</c>),
/// so the Claude Code parser is reused. Unlike Claude Code it needs no <c>--verbose</c> for stream-json.
/// Uses the CLI's own login (<c>qodercli</c> then <c>/login</c>) or <c>QODER_PERSONAL_ACCESS_TOKEN</c>.
/// The China edition ships as <c>qoderclicn</c>; point <see cref="AgentOptions.Executable"/> at it if needed.
/// </summary>
public sealed class QoderAdapter : IAgentAdapter
{
    public string Name => "qoder";
    public string DefaultExecutable => "qodercli";
    public string DisplayName => "Qoder";
    public IReadOnlyList<string> DefaultExtraArgs => ["--permission-mode", "accept_edits"];
    public string InstallHint =>
        "Windows PowerShell: irm https://qoder.com/install.ps1 | iex (or npm i -g @qoder-ai/qodercli), "
        + "then run `qodercli` and /login, or set QODER_PERSONAL_ACCESS_TOKEN";

    public AgentInvocation BuildInvocation(AgentTurnContext context)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json" };
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

    public IAgentOutputParser CreateParser() => new ClaudeCodeAdapter.Parser();

    public string? GetWatchCommand(AgentTask task, AgentOptions options) =>
        task.SessionId is null || task.WorktreePath is null
            ? null
            : $"cd \"{task.WorktreePath}\" && {options.Executable ?? DefaultExecutable} --resume {task.SessionId}";
}
