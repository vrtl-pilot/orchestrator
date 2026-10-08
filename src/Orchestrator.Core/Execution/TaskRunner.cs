using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Events;
using Orchestrator.Core.Git;
using Orchestrator.Core.Model;
using Orchestrator.Core.Processes;
using Orchestrator.Core.Storage;

namespace Orchestrator.Core.Execution;

/// <summary>
/// Background worker: takes queued tasks, prepares a worktree, runs one agent turn as a child process,
/// streams normalized events, then commits, diffs and tests the result.
/// </summary>
public sealed class TaskRunner(
    ITaskStore store,
    TaskEventLog events,
    TaskCoordination coordination,
    AgentRegistry agents,
    WorktreeManager worktrees,
    IOptions<OrchestratorOptions> options,
    ILogger<TaskRunner> logger) : BackgroundService
{
    private readonly OrchestratorOptions _options = options.Value;
    private readonly SemaphoreSlim _globalSlots = new(Math.Max(1, options.Value.MaxConcurrentTasks));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();

    private string ScratchRoot => Path.Combine(_options.DataDirectory, "scratch");
    private string PatchRoot => Path.Combine(_options.DataDirectory, "patches");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var taskId in coordination.Queue.Reader.ReadAllAsync(stoppingToken))
            {
                // A task can be re-queued (answer / follow-up) while its previous turn is still wrapping up.
                // Chain after that run instead of skipping it; RunAsync ignores entries that are no longer Queued.
                var previous = _inFlight.GetValueOrDefault(taskId) ?? Task.CompletedTask;
                var run = Task.Run(async () =>
                {
                    await previous;
                    await RunGuardedAsync(taskId, stoppingToken);
                }, CancellationToken.None);
                _inFlight[taskId] = run;
                _ = run.ContinueWith(
                    completed => _inFlight.TryRemove(new KeyValuePair<string, Task>(taskId, completed)),
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(_inFlight.Values);
    }

    /// <summary>Tasks interrupted by a restart become Failed (resumable with continue); queued ones are re-queued.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var status in new[] { AgentTaskStatus.Preparing, AgentTaskStatus.Running, AgentTaskStatus.Testing })
        {
            foreach (var task in await store.ListAsync(status, limit: 500, ct: ct))
            {
                task.Status = AgentTaskStatus.Failed;
                task.Error = "Interrupted by an orchestrator restart. Use continue_task to resume the agent session.";
                task.CompletedAt = DateTimeOffset.UtcNow;
                await store.SaveAsync(task, ct);
                events.Append(task.Id, AgentEventKind.Status, nameof(AgentTaskStatus.Failed), task.Error);
            }
        }
        foreach (var task in (await store.ListAsync(AgentTaskStatus.Queued, limit: 500, ct: ct)).Reverse())
        {
            await coordination.Queue.Writer.WriteAsync(task.Id, ct);
        }
    }

    private async Task RunGuardedAsync(string taskId, CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(taskId, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Task {TaskId} crashed", taskId);
            await FinishAsync(taskId, t =>
            {
                t.Status = AgentTaskStatus.Failed;
                t.Error = $"Orchestrator error: {ex.Message}";
            });
        }
    }

    private async Task RunAsync(string taskId, CancellationToken stoppingToken)
    {
        var task = await store.GetAsync(taskId, stoppingToken);
        if (task is null || task.Status != AgentTaskStatus.Queued) return;
        if (!agents.TryGet(task.Agent, out var adapter, out var agentOptions))
        {
            await FinishAsync(taskId, t => { t.Status = AgentTaskStatus.Failed; t.Error = $"Agent '{t.Agent}' is not available."; });
            return;
        }

        var agentSlots = _agentSlots.GetOrAdd(task.Agent, _ => new SemaphoreSlim(Math.Max(1, agentOptions.MaxConcurrent)));
        await _globalSlots.WaitAsync(stoppingToken);
        await agentSlots.WaitAsync(stoppingToken);
        var ct = coordination.RegisterRunning(taskId, stoppingToken);
        try
        {
            // Re-read under the lock: the task may have been cancelled while waiting for a slot.
            using (await coordination.LockAsync(taskId, stoppingToken))
            {
                task = await store.GetAsync(taskId, stoppingToken);
                if (task is null || task.Status != AgentTaskStatus.Queued) return;
                task.StartedAt ??= DateTimeOffset.UtcNow;
                await SetStatusAsync(task, task.WorktreePath is null ? AgentTaskStatus.Preparing : AgentTaskStatus.Running);
            }

            if (task.WorktreePath is null)
            {
                var wt = await worktrees.CreateAsync(task.RepoRoot, task.Id, task.BaseRef, task.IncludeUncommitted, ct);
                task.WorktreePath = wt.Path;
                task.Branch = wt.Branch;
                task.BaseSha = wt.BaseSha;
                task.BaseIncludesUncommitted = wt.IncludesUncommittedChanges;
                events.Append(task.Id, AgentEventKind.Log,
                    $"worktree {wt.Path} on {wt.Branch} from {wt.BaseSha[..Math.Min(12, wt.BaseSha.Length)]}"
                    + (wt.IncludesUncommittedChanges ? " (includes your uncommitted changes)" : ""));
                await SetStatusAsync(task, AgentTaskStatus.Running);
            }

            var turnOutcome = await RunAgentTurnAsync(task, adapter, agentOptions, ct);
            if (turnOutcome is null) return; // Already finalized (cancelled / failed).

            if (TaskBrief.ExtractQuestion(turnOutcome, _options.InputRequestMarker) is { } question)
            {
                await CommitWorkAsync(task, $"orchestrator: work in progress for {task.Id} (waiting for input)", ct);
                task.PendingQuestion = question;
                task.Summary = turnOutcome;
                events.Append(task.Id, AgentEventKind.Log, $"agent needs input: {question}");
                await SetStatusAsync(task, AgentTaskStatus.InputRequired);
                return;
            }

            task.Summary = turnOutcome;
            await CommitWorkAsync(task, $"orchestrator: {task.Agent} result for task {task.Id}", ct);

            if (task.TestCommand is not null)
            {
                await SetStatusAsync(task, AgentTaskStatus.Testing);
                await RunTestsAsync(task, ct);
            }

            task.CompletedAt = DateTimeOffset.UtcNow;
            await SetStatusAsync(task, AgentTaskStatus.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            await FinishAsync(taskId, t => { t.Status = AgentTaskStatus.Cancelled; t.Error = "Cancelled."; }, task);
        }
        finally
        {
            coordination.UnregisterRunning(taskId);
            agentSlots.Release();
            _globalSlots.Release();
        }
    }

    /// <summary>Runs one agent turn. Returns the agent's final message, or null if the task was finalized here.</summary>
    private async Task<string?> RunAgentTurnAsync(AgentTask task, IAgentAdapter adapter, AgentOptions agentOptions, CancellationToken ct)
    {
        var scratch = Path.GetFullPath(Path.Combine(ScratchRoot, task.Id));
        Directory.CreateDirectory(scratch);

        var allowed = AllowedCommandsFor(task);
        var message = task.NextMessage ?? TaskBrief.FirstTurn(task, _options.InputRequestMarker, allowed);
        var invocation = adapter.BuildInvocation(new AgentTurnContext
        {
            Task = task,
            Message = message,
            ResumeSessionId = task.SessionId,
            ScratchDirectory = scratch,
            Options = agentOptions,
            AllowedCommands = allowed,
        });

        var parser = adapter.CreateParser();
        var stderr = new OutputTail(4000);
        events.Append(task.Id, AgentEventKind.Log, $"$ {invocation.Executable} {string.Join(' ', invocation.Arguments)}");

        var env = ScrubbedEnvironment();
        foreach (var (k, v) in invocation.Environment) env[k] = v;
        env["ORCHESTRATOR_TASK_ID"] = task.Id;
        env["ORCHESTRATOR_TASK_DEPTH"] = task.Depth.ToString();

        ProcessOutcome outcome;
        try
        {
            outcome = await RunAgentProcessAsync();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var resolved = ExecutableResolver.Find(invocation.Executable) ?? invocation.Executable;
            await FinishAsync(task.Id, t =>
            {
                t.Status = AgentTaskStatus.Failed;
                t.Error = $"Could not start '{resolved}' ({ex.Message}). Check that the {t.Agent} CLI is installed and set "
                          + $"Orchestrator:Agents:{t.Agent}:Executable to its full path (on Windows, the .cmd or .exe file).";
            }, task);
            return null;
        }

        Task<ProcessOutcome> RunAgentProcessAsync() => ProcessRunner.RunAsync(
            new ProcessSpec
            {
                FileName = invocation.Executable,
                Arguments = invocation.Arguments,
                WorkingDirectory = task.WorktreePath!,
                StandardInput = invocation.StandardInput,
                Environment = env,
                Timeout = TimeSpan.FromMinutes(task.TimeoutMinutes ?? _options.DefaultTimeoutMinutes),
                IdleTimeout = _options.IdleTimeoutMinutes > 0 ? TimeSpan.FromMinutes(_options.IdleTimeoutMinutes) : null,
            },
            line =>
            {
                foreach (var evt in parser.ParseLine(line))
                {
                    events.Append(task.Id, evt.Kind, evt.Text, evt.Detail);
                }
                if (parser.Model is { } reported && reported != task.ModelUsed)
                {
                    // Show the model as soon as the agent names it, not only when the turn ends.
                    task.ModelUsed = reported;
                    events.Append(task.Id, AgentEventKind.Log, $"model: {reported}");
                    store.SaveAsync(task, CancellationToken.None).GetAwaiter().GetResult();
                    coordination.NotifyChanged(task.Id);
                }
            },
            line =>
            {
                stderr.Add(line);
                if (!string.IsNullOrWhiteSpace(line)) events.Append(task.Id, AgentEventKind.Log, line, "stderr");
            },
            ct);

        task.Turns++;
        task.NextMessage = null;
        if (parser.SessionId is not null) task.SessionId = parser.SessionId;
        task.WatchCommand = adapter.GetWatchCommand(task, agentOptions);
        if (parser.Model is null && outcome.Reason == ProcessEndReason.Exited)
        {
            var model = task.Model ?? agentOptions.Model;  // Passed explicitly, so this is what ran.
            if (model is null)
            {
                try
                {
                    model = await adapter.ResolveModelAsync(task, agentOptions, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogDebug(ex, "Could not resolve the model for task {TaskId}", task.Id);
                }
            }
            if (model is not null && model != task.ModelUsed)
            {
                task.ModelUsed = model;
                events.Append(task.Id, AgentEventKind.Log, $"model: {model}");
            }
        }
        await store.SaveAsync(task, CancellationToken.None);

        var finalMessage = parser.FinalMessage;
        if (invocation.LastMessageFile is not null && File.Exists(invocation.LastMessageFile))
        {
            var fromFile = (await File.ReadAllTextAsync(invocation.LastMessageFile, CancellationToken.None)).Trim();
            if (fromFile.Length > 0) finalMessage = fromFile;
        }

        switch (outcome.Reason)
        {
            case ProcessEndReason.Cancelled:
                await CommitWorkAsync(task, $"orchestrator: partial work of cancelled task {task.Id}", CancellationToken.None);
                await FinishAsync(task.Id, t => { t.Status = AgentTaskStatus.Cancelled; t.Error = "Cancelled."; }, task);
                return null;
            case ProcessEndReason.TimedOut:
            case ProcessEndReason.IdleTimedOut:
                await CommitWorkAsync(task, $"orchestrator: partial work of timed-out task {task.Id}", CancellationToken.None);
                await FinishAsync(task.Id, t =>
                {
                    t.Status = AgentTaskStatus.Failed;
                    t.Summary = finalMessage;
                    t.Error = outcome.Reason == ProcessEndReason.TimedOut
                        ? "The agent exceeded its time limit. Any partial work was committed; continue_task can resume it."
                        : $"The agent produced no output for {_options.IdleTimeoutMinutes} minutes and was stopped. Any partial work was committed.";
                }, task);
                return null;
        }

        if (outcome.ExitCode != 0 || (parser.Error is not null && string.IsNullOrWhiteSpace(finalMessage)))
        {
            await CommitWorkAsync(task, $"orchestrator: partial work of failed task {task.Id}", CancellationToken.None);
            await FinishAsync(task.Id, t =>
            {
                t.Status = AgentTaskStatus.Failed;
                t.Summary = finalMessage;
                t.Error = parser.Error ?? $"{t.Agent} exited with code {outcome.ExitCode}. {stderr}".Trim();
            }, task);
            return null;
        }

        return string.IsNullOrWhiteSpace(finalMessage) ? "(the agent finished without a final message)" : finalMessage;
    }

    /// <summary>Configured allowed commands plus the task's own test command, so the agent can run the same check.</summary>
    private IReadOnlyList<string> AllowedCommandsFor(AgentTask task)
    {
        var commands = agents.CurrentOptions.EffectiveAllowedCommands.ToList();
        if (task.TestCommand is { Length: > 0 } test && !commands.Contains(test.Trim())) commands.Add(test.Trim());
        return commands;
    }

    private async Task CommitWorkAsync(AgentTask task, string message, CancellationToken ct)
    {
        if (task.WorktreePath is null || task.BaseSha is null || !Directory.Exists(task.WorktreePath)) return;
        try
        {
            if (await worktrees.CommitAllAsync(task.WorktreePath, message, ct))
            {
                events.Append(task.Id, AgentEventKind.Log, "committed uncommitted agent changes");
            }
            var diff = await worktrees.DiffAsync(
                task.WorktreePath, task.BaseSha, Path.Combine(PatchRoot, $"{task.Id}.patch"), ct);
            task.HeadSha = diff.HeadSha;
            task.DiffStat = diff.DiffStat;
            task.ChangedFiles = diff.Files.ToList();
            task.PatchPath = diff.PatchPath;
            events.Append(task.Id, AgentEventKind.Log,
                diff.Files.Count == 0 ? "no file changes" : $"{diff.Files.Count} file(s) changed", diff.DiffStat);
        }
        catch (GitException ex)
        {
            events.Append(task.Id, AgentEventKind.Error, "git: could not record changes", ex.Message);
        }
    }

    private async Task RunTestsAsync(AgentTask task, CancellationToken ct)
    {
        var (shell, shellArgs, shellLabel) = ShellFor(task.TestCommand!);
        var tail = new OutputTail(6000);
        events.Append(task.Id, AgentEventKind.Test, $"$ [{shellLabel}] {task.TestCommand}");

        var outcome = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = task.WorktreePath!,
                Environment = new Dictionary<string, string?>(ScrubbedEnvironment()) { ["ORCHESTRATOR_TASK_ID"] = task.Id, ["CI"] = "true" },
                Timeout = TimeSpan.FromMinutes(Math.Max(5, task.TimeoutMinutes ?? _options.DefaultTimeoutMinutes)),
            },
            line => { tail.Add(line); events.Append(task.Id, AgentEventKind.Test, line); },
            line => { tail.Add(line); events.Append(task.Id, AgentEventKind.Test, line, "stderr"); },
            ct);

        task.TestExitCode = outcome.ExitCode;
        task.TestsPassed = outcome.Succeeded;
        task.TestOutputTail = tail.ToString();
        events.Append(task.Id, AgentEventKind.Test, task.TestsPassed == true ? "tests passed" : $"tests failed ({outcome.Reason}, exit {outcome.ExitCode})");
    }

    /// <summary>Inherited variables that must not leak into agents (null = remove), see <see cref="OrchestratorOptions.ScrubEnvironmentVariables"/>.</summary>
    internal Dictionary<string, string?> ScrubbedEnvironment() => EnvironmentScrubber.Scrubbed(_options.ScrubEnvironmentVariables);

    /// <summary>
    /// Shell for test commands, with a label for the event log. On Windows prefer Git Bash, so the POSIX-style
    /// commands agents and callers usually write (grep, test -f, &&) work; cmd.exe is the last resort.
    /// </summary>
    private (string Shell, string[] Args, string Label) ShellFor(string command)
    {
        if (_options.Shell is { Length: > 0 } configured)
        {
            var parts = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (parts[0], [.. parts.Skip(1), command], "configured");
        }
        if (OperatingSystem.IsWindows())
        {
            return ExecutableResolver.FindGitBash() is { } gitBash
                ? (gitBash, ["-lc", command], "git-bash")
                : ("cmd.exe", ["/d", "/s", "/c", command], "cmd");
        }
        return ExecutableResolver.Find("bash") is not null ? ("bash", ["-lc", command], "bash") : ("sh", ["-c", command], "sh");
    }
    private async Task SetStatusAsync(AgentTask task, AgentTaskStatus status)
    {
        task.Status = status;
        await store.SaveAsync(task, CancellationToken.None);
        events.Append(task.Id, AgentEventKind.Status, status.ToString(), status == AgentTaskStatus.InputRequired ? task.PendingQuestion : null);
        coordination.NotifyChanged(task.Id);
    }

    /// <summary>Applies a terminal update, reusing the in-memory task when available so no field is lost.</summary>
    private async Task FinishAsync(string taskId, Action<AgentTask> update, AgentTask? current = null)
    {
        var task = current ?? await store.GetAsync(taskId, CancellationToken.None);
        if (task is null) return;
        update(task);
        task.CompletedAt = DateTimeOffset.UtcNow;
        await SetStatusAsync(task, task.Status);
        if (task.Error is not null) events.Append(taskId, AgentEventKind.Error, task.Error);
    }
}
