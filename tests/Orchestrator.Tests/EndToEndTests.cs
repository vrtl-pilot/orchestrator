using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Git;
using Orchestrator.Core.Model;
using Orchestrator.Core.Storage;

namespace Orchestrator.Tests;

/// <summary>
/// Full delegate → input-required → answer → resume → commit → test cycle, using a scripted fake agent that
/// speaks Claude Code's stream-json format. Runs the real runner, git worktrees, SQLite store and event log.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "orch-data-" + Guid.NewGuid().ToString("N")[..8]);
    private TestRepo _repo = null!;
    private FakeAgent _agent = null!;
    private TaskService _service = null!;
    private TaskEventLog _events = null!;
    private TaskRunner _runner = null!;

    public async Task InitializeAsync()
    {
        _repo = await TestRepo.CreateAsync();
        var options = Options.Create(new OrchestratorOptions
        {
            DataDirectory = _data,
            IdleTimeoutMinutes = 1,
            Agents = { ["fake"] = new AgentOptions(), ["broken"] = new AgentOptions() },
        });
        var store = new SqliteTaskStore(Path.Combine(_data, "orchestrator.db"));
        _events = new TaskEventLog(_data);
        var coordination = new TaskCoordination();
        _agent = new FakeAgent();
        var registry = new AgentRegistry([_agent, new MissingAgent(), new BrokenAgent(_data)], options);
        var worktrees = new WorktreeManager(options);
        _service = new TaskService(store, _events, coordination, registry, worktrees, options);
        _runner = new TaskRunner(store, _events, coordination, registry, worktrees, options, NullLogger<TaskRunner>.Instance);
        await _runner.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _runner.StopAsync(CancellationToken.None);
        _runner.Dispose();
        _repo.Dispose();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Delegated_agent_can_ask_a_question_and_resume_after_the_answer()
    {
        if (OperatingSystem.IsWindows()) return; // The fake agent is a POSIX shell script.

        // Simulate the orchestrator running inside a Claude Code session: the child must not inherit its identity.
        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "parent-session");

        var task = await _service.DelegateAsync(new DelegateRequest
        {
            Agent = "fake",
            Prompt = "Create the file.",
            RepoPath = _repo.Root,
            TestCommand = "test -f fake.txt",
        });

        var waiting = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));
        Assert.Equal(AgentTaskStatus.InputRequired, waiting.Status);
        Assert.Equal("Should the file be called fake.txt?", waiting.PendingQuestion);
        Assert.Equal("fake-session", waiting.SessionId);

        await _service.AnswerAsync(task.Id, "Yes, fake.txt.");
        var done = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));

        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        Assert.Equal("Created fake.txt as requested.", done.Summary);
        Assert.True(done.TestsPassed);
        Assert.Equal(2, done.Turns);
        Assert.Contains(done.ChangedFiles, f => f.Path == "fake.txt");
        Assert.Equal([null, "fake-session"], _agent.ResumedSessions);
        Assert.Contains("Answer from the requester: Yes, fake.txt.", _agent.Messages[1]);

        // The caller's repo is untouched; the work is on the task branch, ready to merge.
        Assert.False(File.Exists(Path.Combine(_repo.Root, "fake.txt")));
        Assert.Contains("fake.txt", await _repo.GitAsync("show", "--stat", "--format=", done.Branch!));
        Assert.Equal("parent session: none", await _repo.GitAsync("show", $"{done.Branch}:fake.txt"));
        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", null);

        var kinds = _events.Read(task.Id).Select(e => (e.Kind, e.Text)).ToList();
        Assert.Contains((AgentEventKind.Status, nameof(AgentTaskStatus.InputRequired)), kinds);
        Assert.Contains((AgentEventKind.FileChange, "Write fake.txt"), kinds);
        Assert.Contains((AgentEventKind.Status, nameof(AgentTaskStatus.Completed)), kinds);
    }

    [Fact]
    public async Task Answer_sent_the_instant_a_question_appears_is_not_lost()
    {
        if (OperatingSystem.IsWindows()) return;

        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "Create the file.", RepoPath = _repo.Root });
        // Poll tightly and answer as soon as the status flips, racing the runner's bookkeeping.
        AgentTask? current = null;
        for (var i = 0; i < 600; i++)
        {
            current = await _service.GetAsync(task.Id);
            if (current!.Status == AgentTaskStatus.InputRequired) break;
            await Task.Delay(10);
        }
        Assert.Equal(AgentTaskStatus.InputRequired, current!.Status);
        await _service.AnswerAsync(task.Id, "Yes.");

        var done = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));
        Assert.Equal(AgentTaskStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Running_task_can_be_cancelled()
    {
        if (OperatingSystem.IsWindows()) return;

        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "SLEEP please", RepoPath = _repo.Root });
        for (var i = 0; i < 100 && (await _service.GetAsync(task.Id))!.Status != AgentTaskStatus.Running; i++)
        {
            await Task.Delay(100);
        }

        await _service.CancelAsync(task.Id);
        var result = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(30));

        Assert.Equal(AgentTaskStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Delegating_to_an_agent_whose_cli_is_missing_fails_before_creating_anything()
    {
        var ex = await Assert.ThrowsAsync<OrchestratorException>(() =>
            _service.DelegateAsync(new DelegateRequest { Agent = "missing", Prompt = "x", RepoPath = _repo.Root }));

        Assert.Contains("was not found", ex.Message);
        Assert.Contains("npm i -g missing-agent", ex.Message);
        Assert.Empty(await _service.ListAsync());
        Assert.DoesNotContain("orchestrator/", await _repo.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task Cleanup_keeps_an_unmerged_branch_and_deletes_it_once_merged()
    {
        if (OperatingSystem.IsWindows()) return;

        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "DONE", RepoPath = _repo.Root });
        var done = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));
        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        var branch = done.Branch!;

        var kept = await _service.CleanupAsync(task.Id, deleteBranch: true);
        Assert.Null(kept.WorktreePath);
        Assert.Equal(branch, kept.Branch);
        Assert.Contains("not merged, patch not applied", kept.Notice);
        Assert.Contains(branch, await _repo.GitAsync("branch", "--list"));

        await _repo.GitAsync("merge", "-q", "--no-ff", "-m", "merge", branch);
        var deleted = await _service.CleanupAsync(task.Id, deleteBranch: true);
        Assert.Null(deleted.Branch);
        Assert.Contains("deleted (integrated)", deleted.Notice);
        Assert.DoesNotContain(branch, await _repo.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task Task_based_on_uncommitted_work_is_integrated_by_applying_its_patch()
    {
        if (OperatingSystem.IsWindows()) return;

        // The caller has an uncommitted edit; the task starts from a snapshot that includes it.
        _repo.Write("README.md", "hello\nlocal edit\n");
        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "DONE", RepoPath = _repo.Root });
        var done = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));
        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        Assert.True(done.BaseIncludesUncommitted);

        var view = Orchestrator.Api.TaskView.From(done, publicUrl: null);
        Assert.Contains("do NOT git merge", view.NextStep);

        // The patch holds only the agent's change and applies onto the still-dirty working tree.
        var patch = await File.ReadAllTextAsync(done.PatchPath!);
        Assert.Contains("done.txt", patch);
        Assert.DoesNotContain("local edit", patch);
        await _repo.GitAsync("apply", done.PatchPath!);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "done.txt")));
        Assert.Equal("hello\nlocal edit\n", File.ReadAllText(Path.Combine(_repo.Root, "README.md")));

        // Cleanup recognises the applied patch as integrated and deletes the branch without force.
        var cleaned = await _service.CleanupAsync(task.Id, deleteBranch: true);
        Assert.Null(cleaned.Branch);
        Assert.Contains("deleted (integrated)", cleaned.Notice);
    }

    [Fact]
    public async Task Model_used_is_recorded_from_the_agent_or_the_request()
    {
        if (OperatingSystem.IsWindows()) return;

        // MODEL: the fake agent names its model in the init frame, like Claude Code / Qoder.
        var reported = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "MODEL", RepoPath = _repo.Root });
        var r = await _service.WaitAsync(reported.Id, TimeSpan.FromSeconds(60));
        Assert.Equal("fake-model-1", r.ModelUsed);
        Assert.Contains(_events.Read(reported.Id), e => e.Text == "model: fake-model-1");

        // DONE does not name a model: the requested one (passed to the CLI) is recorded.
        var requested = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "DONE", RepoPath = _repo.Root, Model = "my-model" });
        var q = await _service.WaitAsync(requested.Id, TimeSpan.FromSeconds(60));
        Assert.Equal("my-model", q.ModelUsed);
    }

    [Fact]
    public async Task Cleanup_with_force_discards_an_unmerged_branch()
    {
        if (OperatingSystem.IsWindows()) return;

        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "DONE", RepoPath = _repo.Root });
        var done = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(60));

        var result = await _service.CleanupAsync(task.Id, deleteBranch: true, force: true);

        Assert.Null(result.Branch);
        Assert.Contains("discarded", result.Notice);
        Assert.DoesNotContain(done.Branch!, await _repo.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task Agent_that_cannot_be_started_fails_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows()) return;

        var task = await _service.DelegateAsync(new DelegateRequest { Agent = "broken", Prompt = "x", RepoPath = _repo.Root });
        var result = await _service.WaitAsync(task.Id, TimeSpan.FromSeconds(30));

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.StartsWith("Could not start", result.Error);
        Assert.Contains("Orchestrator:Agents:broken:Executable", result.Error);
    }

    [Fact]
    public async Task Repository_without_commits_is_rejected_before_anything_is_created()
    {
        var empty = Directory.CreateTempSubdirectory("orch-empty-").FullName;
        try
        {
            await GitClient.RunCheckedAsync(empty, ["init", "-q"]);

            var ex = await Assert.ThrowsAsync<OrchestratorException>(() =>
                _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "x", RepoPath = empty }));

            Assert.Contains("has no commits yet", ex.Message);
            Assert.Empty(await _service.ListAsync());
            Assert.False(Directory.Exists(Path.Combine(empty, ".orchestrator")));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Same_client_request_id_returns_the_same_task()
    {
        var a = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "SLEEP", RepoPath = _repo.Root, ClientRequestId = "k1" });
        var b = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "SLEEP", RepoPath = _repo.Root, ClientRequestId = "k1" });
        Assert.Equal(a.Id, b.Id);
        await _service.CancelAsync(a.Id);
    }

    [Fact]
    public async Task Unknown_agent_and_excessive_depth_are_rejected()
    {
        await Assert.ThrowsAsync<OrchestratorException>(() =>
            _service.DelegateAsync(new DelegateRequest { Agent = "nope", Prompt = "x", RepoPath = _repo.Root }));

        var parent = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "SLEEP", RepoPath = _repo.Root });
        var child = await _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "SLEEP", RepoPath = _repo.Root, ParentTaskId = parent.Id });
        var ex = await Assert.ThrowsAsync<OrchestratorException>(() =>
            _service.DelegateAsync(new DelegateRequest { Agent = "fake", Prompt = "x", RepoPath = _repo.Root, ParentTaskId = child.Id }));
        Assert.Contains("depth", ex.Message);

        await _service.CancelAsync(parent.Id);
        await _service.CancelAsync(child.Id);
    }

    /// <summary>An "executable" that exists and has the execute bit but is not a valid program.</summary>
    private sealed class BrokenAgent : IAgentAdapter
    {
        private readonly string _path;

        public BrokenAgent(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "broken-agent");
            File.WriteAllBytes(_path, [0x00, 0x01, 0x02, 0x03]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public string Name => "broken";
        public string DefaultExecutable => _path;
        public AgentInvocation BuildInvocation(AgentTurnContext context) =>
            new() { Executable = _path, Arguments = [], StandardInput = context.Message };
        public IAgentOutputParser CreateParser() => new ClaudeCodeAdapter().CreateParser();
        public string? GetWatchCommand(AgentTask task, AgentOptions options) => null;
    }

    private sealed class MissingAgent : IAgentAdapter
    {
        public string Name => "missing";
        public string DefaultExecutable => "definitely-not-installed-agent-cli";
        public string InstallHint => "npm i -g missing-agent";
        public AgentInvocation BuildInvocation(AgentTurnContext context) => throw new InvalidOperationException();
        public IAgentOutputParser CreateParser() => throw new InvalidOperationException();
        public string? GetWatchCommand(AgentTask task, AgentOptions options) => null;
    }

    /// <summary>
    /// First turn: asks a question via NEEDS_INPUT. Second turn (an answer): writes fake.txt and finishes.
    /// "SLEEP" in the prompt: sleeps, to exercise cancellation.
    /// </summary>
    private sealed class FakeAgent : IAgentAdapter
    {
        private const string Script = """
            input=$(cat)
            case "$input" in
              *SLEEP*) sleep 30 ;;
              *MODEL*)
                printf '%s\n' '{"type":"system","subtype":"init","session_id":"fake-session","model":"fake-model-1"}'
                printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"fake-session"}'
                ;;
              *DONE*)
                echo "done" > done.txt
                printf '%s\n' '{"type":"system","subtype":"init","session_id":"fake-session"}'
                printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Wrote done.txt.","session_id":"fake-session"}'
                ;;
              *"Answer from the requester"*)
                echo "parent session: ${CLAUDE_CODE_SESSION_ID:-none}" > fake.txt
                printf '%s\n' '{"type":"system","subtype":"init","session_id":"fake-session"}'
                printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"fake.txt"}}]}}'
                printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Created fake.txt as requested.","session_id":"fake-session"}'
                ;;
              *)
                printf '%s\n' '{"type":"system","subtype":"init","session_id":"fake-session"}'
                printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"I need a decision.\nNEEDS_INPUT: Should the file be called fake.txt?","session_id":"fake-session"}'
                ;;
            esac
            """;

        public List<string?> ResumedSessions { get; } = [];
        public List<string> Messages { get; } = [];

        public string Name => "fake";
        public string DefaultExecutable => "sh";

        public AgentInvocation BuildInvocation(AgentTurnContext context)
        {
            ResumedSessions.Add(context.ResumeSessionId);
            Messages.Add(context.Message);
            return new AgentInvocation { Executable = "sh", Arguments = ["-c", Script], StandardInput = context.Message };
        }

        public IAgentOutputParser CreateParser() => new ClaudeCodeAdapter().CreateParser();

        public string? GetWatchCommand(AgentTask task, AgentOptions options) => null;
    }
}
