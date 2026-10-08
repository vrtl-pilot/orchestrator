namespace Orchestrator.Core;

/// <summary>Bound from the <c>Orchestrator</c> configuration section.</summary>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>
    /// Where the task database, event logs and patches are stored. Empty means <see cref="DefaultDataDirectory"/>, a
    /// fixed per-user folder, so every way of starting the server (any working directory) sees the same tasks.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Same per-user folder as <see cref="Setup.OrchestratorPaths.Root"/>.</summary>
    public static string DefaultDataDirectory => Setup.OrchestratorPaths.Root;

    /// <summary>Server log file. Empty = <c>{DataDirectory}/logs/server.log</c>; <c>"off"</c> disables file logging.</summary>
    public string LogFile { get; set; } = "";

    /// <summary>Repository used when a request does not name one.</summary>
    public string? DefaultRepository { get; set; }

    /// <summary>If non-empty, only repositories under these roots may be targeted.</summary>
    public List<string> AllowedRepositoryRoots { get; set; } = [];

    /// <summary>Worktree location relative to the repository root. Added to <c>.git/info/exclude</c>.</summary>
    public string WorktreeDirectory { get; set; } = ".orchestrator/worktrees";

    /// <summary>Prefix of the per-task branch (<c>{prefix}{taskId}</c>).</summary>
    public string BranchPrefix { get; set; } = "orchestrator/";

    public int MaxConcurrentTasks { get; set; } = 3;
    public int DefaultTimeoutMinutes { get; set; } = 60;

    /// <summary>Kill an agent that produces no output for this long. 0 disables.</summary>
    public int IdleTimeoutMinutes { get; set; } = 15;

    /// <summary>Maximum delegation depth (a delegate delegating again counts as depth 2).</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// Agents end their final message with this marker followed by a question when they need a decision.
    /// The orchestrator then moves the task to <c>InputRequired</c>.
    /// </summary>
    public string InputRequestMarker { get; set; } = "NEEDS_INPUT:";

    /// <summary>Files or directories (relative to the repo root) copied into each new worktree, e.g. <c>.env.local</c>.</summary>
    public List<string> CopyIntoWorktree { get; set; } = [];

    /// <summary>Shell used for test commands. Defaults to <c>bash -lc</c> on Unix and <c>cmd /c</c> on Windows.</summary>
    public string? Shell { get; set; }

    /// <summary>Base URL used in links handed to callers (dashboard, events). Defaults to the first listening URL.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Inherited environment variables removed before starting an agent (trailing <c>*</c> = prefix match).
    /// When the orchestrator itself runs inside an agent session (e.g. started from Claude Code's terminal),
    /// variables such as <c>CLAUDE_CODE_SESSION_ID</c> would otherwise make the child reuse the parent's session
    /// identity, transcript and IPC channel. Credentials are not in this list.
    /// </summary>
    public List<string> ScrubEnvironmentVariables { get; set; } =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_REMOTE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_MESSAGING_*",
        "CLAUDE_SESSION_INGRESS_TOKEN_FILE",
        "CLAUDE_CODE_POST_FOR_SESSION_INGRESS_V2",
        "CLAUDE_CODE_SYNC_SESSION_REFS",
        "CLAUDE_CODE_WORKER_EPOCH",
        "CLAUDE_CODE_TEE_SDK_STDOUT",
        "CLAUDE_CODE_INCLUDE_PARTIAL_MESSAGES",
        "CLAUDE_CODE_DIAGNOSTICS_FILE",
        "CLAUDE_AFTER_LAST_COMPACT",
        "CLAUDE_PID",
        "CODEX_THREAD_ID",
        "CODEX_SESSION_ID",
        "OPENCODE_SESSION_ID",
        "QODER_SESSION_*",
        "QODER_PID",
        "OPENCODE_SERVER_PASSWORD",
        "ORCHESTRATOR_API_KEY",
    ];

    /// <summary>Optional shared secret; when set, API and MCP requests must send <c>X-Orchestrator-Key</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Host names accepted in addition to localhost/127.0.0.1/[::1] and the PublicUrl host, e.g. a Docker service
    /// name. Requests with any other Host header are rejected (protects against DNS rebinding).
    /// </summary>
    public List<string> TrustedHosts { get; set; } = [];

    public Dictionary<string, AgentOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-agent settings (<c>Orchestrator:Agents:claude</c>, <c>...:codex</c>, <c>...:opencode</c>, <c>...:qoder</c>).</summary>
public sealed class AgentOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Executable name or full path. Resolved on PATH (including <c>.cmd</c> shims on Windows).</summary>
    public string? Executable { get; set; }

    /// <summary>Default model when the request does not specify one.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Extra arguments appended to every invocation. This is where the permission policy lives, e.g.
    /// <c>--permission-mode acceptEdits</c> for Claude or <c>--sandbox workspace-write</c> for Codex.
    /// </summary>
    public List<string>? ExtraArgs { get; set; }

    /// <summary>Extra environment variables for the agent process.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    public int MaxConcurrent { get; set; } = 2;

    /// <summary>
    /// OpenCode only: URL of a running <c>opencode serve</c>. When set, tasks run inside that server so you can
    /// watch them live with <c>opencode attach {url} --session {id}</c>.
    /// </summary>
    public string? AttachUrl { get; set; }

    // ---- Custom platforms (agents without a built-in adapter) -------------------------------------------------
    // Any CLI can be added from the Setup page or config.json without code. Placeholders: {model}, {sessionId},
    // {prompt}. See ConfiguredAgentAdapter.

    /// <summary>Name shown in the UI, e.g. "Gemini CLI".</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Output protocol of a custom agent: <c>claude-stream-json</c>, <c>codex-jsonl</c>, <c>opencode-json</c> or <c>text</c>.
    /// Ignored for built-in agents.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>Base arguments for one non-interactive turn, e.g. <c>["-p", "--output-format", "stream-json"]</c>.</summary>
    public List<string>? Args { get; set; }

    /// <summary>Added when a model is requested, e.g. <c>["--model", "{model}"]</c>.</summary>
    public List<string>? ModelArgs { get; set; }

    /// <summary>Added to resume a session, e.g. <c>["--resume", "{sessionId}"]</c>. Without it, answers start a new turn with context.</summary>
    public List<string>? ResumeArgs { get; set; }

    /// <summary><c>stdin</c> (default) or <c>arg</c> (appended last, or wherever <c>{prompt}</c> appears in Args).</summary>
    public string? PromptVia { get; set; }

    /// <summary>Arguments for the Setup page's "Check" button (default <c>["--version"]</c>).</summary>
    public List<string>? VersionArgs { get; set; }

    public string? InstallHint { get; set; }

    /// <summary>How to connect this platform to the orchestrator (MCP registration + skill folders).</summary>
    public IntegrationOptions? Integration { get; set; }
}

/// <summary>
/// How the setup wizard / Setup page connects an agent to the orchestrator. Built-in agents have defaults in code;
/// custom agents describe it here. Placeholders: {url} (MCP endpoint), {name} (server name "orchestrator").
/// </summary>
public sealed class IntegrationOptions
{
    /// <summary>Command (program + args) that registers the MCP server, e.g. <c>["gemini","mcp","add","orchestrator","{url}"]</c>.</summary>
    public List<string>? McpAdd { get; set; }

    /// <summary>Command that removes the registration.</summary>
    public List<string>? McpRemove { get; set; }

    /// <summary>User skill folders (<c>~</c> = home); the agent-orchestrator skill is written into each.</summary>
    public List<string>? SkillsDirs { get; set; }
}
