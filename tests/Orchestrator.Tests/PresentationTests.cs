using Orchestrator.Api;
using Orchestrator.Core.Events;
using Orchestrator.Core.Model;

namespace Orchestrator.Tests;

public class PresentationTests
{
    [Fact]
    public void Ansi_colour_codes_are_stripped_from_events()
    {
        var dir = Directory.CreateTempSubdirectory("orch-events-").FullName;
        try
        {
            var log = new TaskEventLog(dir);
            log.Append("t1", AgentEventKind.Command, "\x1B[31;1mgrep: \x1B[31;1mThe term 'grep' is not recognized\x1B[0m",
                "\x1B]0;title\x07\x1B[32mok\x1B[0m");

            var evt = Assert.Single(log.Read("t1"));
            Assert.Equal("grep: The term 'grep' is not recognized", evt.Text);
            Assert.Equal("ok", evt.Detail);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Completed_task_with_failing_tests_needs_attention_and_says_do_not_merge()
    {
        var task = new AgentTask
        {
            Id = "t1", Agent = "opencode", Prompt = "p", RepoRoot = "/r",
            Status = AgentTaskStatus.Completed, TestsPassed = false, TestExitCode = 1, Branch = "orchestrator/t1",
        };

        var view = TaskView.From(task, publicUrl: null);

        Assert.True(view.NeedsAttention);
        Assert.Contains("Tests FAILED", view.NextStep);
        Assert.Contains("Do not merge", view.NextStep);
    }

    [Fact]
    public void Completed_task_with_passing_tests_is_ready_to_merge()
    {
        var task = new AgentTask
        {
            Id = "t1", Agent = "codex", Prompt = "p", RepoRoot = "/r",
            Status = AgentTaskStatus.Completed, TestsPassed = true, Branch = "orchestrator/t1",
        };

        var view = TaskView.From(task, publicUrl: null);

        Assert.False(view.NeedsAttention);
        Assert.Contains("git merge --no-ff orchestrator/t1", view.NextStep);
    }
}
