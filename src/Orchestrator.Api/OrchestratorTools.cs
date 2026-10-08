using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Orchestrator.Core;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Model;

namespace Orchestrator.Api;

/// <summary>
/// MCP front door. Claude Code (or any MCP client: OpenCode, Codex) delegates work with <c>delegate_task</c>,
/// then polls with the bounded <c>wait_task</c>. No tool blocks for more than ~90 seconds, so calls stay inside
/// client idle timeouts, and a question from the delegated agent comes back as <c>input_required</c>
/// instead of deadlocking the caller.
/// </summary>
[McpServerToolType]
public sealed class OrchestratorTools(TaskService tasks, IOptions<OrchestratorOptions> options)
{
    private readonly string? _publicUrl = options.Value.PublicUrl;

    [McpServerTool(Name = "delegate_task", Destructive = false, OpenWorld = false)]
    [Description("""
        Delegate a coding subtask to another coding agent platform (claude, codex, opencode, qoder or a custom one).
        Keep each task small: split larger work into several small, independent subtasks (one concern, a few files, a
        clear check) and delegate each separately, in parallel when they touch different files, instead of one big task.
        Choosing the platform: if the user has not named one, do NOT pick yourself. Call list_agents (or call this tool
        without agent, which starts nothing and returns the choices), ask the user which available platform to use,
        then call this tool with agent set.
        The orchestrator creates an isolated git worktree + branch from your current HEAD (including your uncommitted
        changes by default), runs the agent there, commits its changes, optionally runs a test command, and returns a
        task id immediately. Then call wait_task. Write a self-contained brief: goal, constraints, relevant files,
        acceptance criteria. Your own working tree is never modified; you integrate the result yourself (see nextStep).
        """)]
    public async Task<string> DelegateTask(
        [Description("Self-contained brief for ONE small, well-scoped subtask (goal, constraints, files, acceptance criteria).")] string prompt,
        [Description("Platform chosen by the user: 'claude', 'codex', 'opencode', 'qoder' or a custom name from list_agents. Leave empty to get the choices to ask the user.")] string? agent = null,
        [Description("Absolute path of the repository (any path inside it). Use your current working directory.")] string? repo_path = null,
        [Description("Optional commit-ish to branch from instead of your current HEAD + uncommitted changes.")] string? base_ref = null,
        [Description("Include your uncommitted/untracked changes in the base (default true). Ignored when base_ref is set.")] bool include_uncommitted = true,
        [Description("Optional model override for the delegated agent (agent-specific format, e.g. 'provider/model' for opencode).")] string? model = null,
        [Description("Optional shell command the orchestrator runs in the worktree afterwards, e.g. 'dotnet test' or 'npm test'.")] string? test_command = null,
        [Description("Optional time limit in minutes.")] int? timeout_minutes = null,
        [Description("Set to ORCHESTRATOR_TASK_ID from your environment if you are yourself a delegated agent.")] string? parent_task_id = null,
        [Description("Optional idempotency key; retrying with the same key returns the existing task.")] string? client_request_id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            // Not an error: tell the caller what to ask the user.
            return Json(tasks.AgentChoices());
        }

        var task = await Guard(() => tasks.DelegateAsync(new DelegateRequest
        {
            Agent = agent,
            Prompt = prompt,
            RepoPath = repo_path,
            BaseRef = base_ref,
            IncludeUncommitted = include_uncommitted,
            Model = model,
            TestCommand = test_command,
            TimeoutMinutes = timeout_minutes,
            ParentTaskId = parent_task_id ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_TASK_ID"),
            ClientRequestId = client_request_id,
        }, cancellationToken));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "wait_task", ReadOnly = true)]
    [Description("""
        Wait (up to max_wait_seconds, default 60, max 90) for a delegated task to settle, then return its state.
        Returns early when the task completes, fails, is cancelled, or needs input (status 'input_required' with
        pendingQuestion; answer it with answer_task). If it is still running when the wait ends, call wait_task
        again or do other work in the meantime. Follow the nextStep field.
        """)]
    public async Task<string> WaitTask(
        string task_id,
        [Description("Seconds to wait before returning the current state (1-90).")] int max_wait_seconds = 60,
        CancellationToken cancellationToken = default)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(max_wait_seconds, 1, 90));
        var task = await Guard(() => tasks.WaitAsync(task_id, wait, cancellationToken));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "get_task", ReadOnly = true)]
    [Description("Return the current state of a delegated task without waiting.")]
    public async Task<string> GetTask(string task_id, CancellationToken cancellationToken = default)
    {
        var task = await Guard(async () => await tasks.GetAsync(task_id, cancellationToken) ?? throw new TaskNotFoundException(task_id));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true)]
    [Description("List recent delegated tasks (newest first). Use after a restart or context compaction to find task ids again.")]
    public async Task<string> ListTasks(
        [Description("Optional status filter: queued, preparing, running, input_required, testing, completed, failed, cancelled.")] string? status = null,
        [Description("Optional parent task id filter.")] string? parent_task_id = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        AgentTaskStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AgentTaskStatus>(status.Replace("_", ""), ignoreCase: true, out var parsed))
            {
                throw new McpException($"Unknown status '{status}'.");
            }
            filter = parsed;
        }
        var list = await tasks.ListAsync(filter, parent_task_id, Math.Clamp(limit, 1, 100), cancellationToken);
        return Json(list.Select(t => TaskView.From(t, _publicUrl, compact: true)));
    }

    [McpServerTool(Name = "answer_task")]
    [Description("Answer the pendingQuestion of a task in status 'input_required'. The delegated agent resumes in the same session and worktree. Then call wait_task.")]
    public async Task<string> AnswerTask(
        string task_id,
        [Description("Your decision or answer, phrased for the delegated agent.")] string answer,
        CancellationToken cancellationToken = default)
    {
        var task = await Guard(() => tasks.AnswerAsync(task_id, answer, cancellationToken));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "continue_task")]
    [Description("Send a follow-up instruction to a completed or failed task. The same agent session continues in the same worktree/branch. Then call wait_task.")]
    public async Task<string> ContinueTask(string task_id, string message, CancellationToken cancellationToken = default)
    {
        var task = await Guard(() => tasks.ContinueAsync(task_id, message, cancellationToken));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "cancel_task", Destructive = true)]
    [Description("Cancel a queued, running or input-required task. The agent's process tree is killed; partial work stays on the branch.")]
    public async Task<string> CancelTask(string task_id, CancellationToken cancellationToken = default)
    {
        var task = await Guard(() => tasks.CancelAsync(task_id, cancellationToken));
        return Json(TaskView.From(task, _publicUrl));
    }

    [McpServerTool(Name = "cleanup_task", Destructive = true)]
    [Description("""
        Remove a finished task's worktree after you have merged (or decided to discard) its branch.
        delete_branch=true deletes the branch only if it is merged into the repository's current HEAD; otherwise the
        branch is kept and the notice field says so. Pass force=true only to deliberately discard unmerged work.
        """)]
    public async Task<string> CleanupTask(string task_id, bool delete_branch = false, bool force = false, CancellationToken cancellationToken = default)
    {
        var task = await Guard(() => tasks.CleanupAsync(task_id, delete_branch, force, cancellationToken));
        return Json(TaskView.From(task, _publicUrl, compact: true));
    }

    [McpServerTool(Name = "get_task_events", ReadOnly = true)]
    [Description("Live progress of a task: the delegated agent's messages, commands, file edits, errors and test output (normalized across agents). Pass after_seq from the last event you saw to get only new ones.")]
    public Task<string> GetTaskEvents(string task_id, long after_seq = 0, int limit = 40)
    {
        var list = tasks.GetEvents(task_id, after_seq, Math.Clamp(limit, 1, 200))
            .Select(e => new { e.Seq, e.Kind, Text = Cap(e.Text, 500), Detail = Cap(e.Detail, 500) });
        return Task.FromResult(Json(list));
    }

    [McpServerTool(Name = "list_agents", ReadOnly = true)]
    [Description("""
        List the coding agent platforms this orchestrator can run: which are available (installed and enabled), their
        default model, and how to install the missing ones. Use it to ask the user which platform to delegate to.
        """)]
    public string ListAgents() => Json(tasks.AgentChoices() with
    {
        Status = "ok",
        Instruction = "Ask the user which available platform to use unless they already said; then call delegate_task with agent set.",
    });

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OrchestratorException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string? Cap(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, OrchestratorJson.Options);
}
