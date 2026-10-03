using Orchestrator.Core.Execution;
using Orchestrator.Core.Model;

namespace Orchestrator.Tests;

public class TaskBriefTests
{
    private const string Marker = "NEEDS_INPUT:";

    [Theory]
    [InlineData("I looked around.\nNEEDS_INPUT: Postgres or SQLite?", "Postgres or SQLite?")]
    [InlineData("Summary...\n**NEEDS_INPUT:** Keep the old API?", "Keep the old API?")]
    [InlineData("`NEEDS_INPUT: which port?`", "which port?")]
    [InlineData("needs_input: lower case works", "lower case works")]
    public void Extracts_question_from_marker_line(string message, string expected) =>
        Assert.Equal(expected, TaskBrief.ExtractQuestion(message, Marker));

    [Theory]
    [InlineData("All done, tests pass.")]
    [InlineData("I will write NEEDS_INPUT: when blocked, but I am not blocked.")]
    [InlineData("NEEDS_INPUT:")]
    [InlineData(null)]
    public void Ignores_messages_without_a_real_question(string? message) =>
        Assert.Null(TaskBrief.ExtractQuestion(message, Marker));

    [Fact]
    public void First_turn_brief_contains_workspace_rules_and_marker()
    {
        var task = new AgentTask
        {
            Id = "t1", Agent = "codex", Prompt = "Add a /health endpoint.", RepoRoot = "/repo",
            WorktreePath = "/repo/.orchestrator/worktrees/t1", Branch = "orchestrator/t1", TestCommand = "dotnet test",
        };

        var brief = TaskBrief.FirstTurn(task, Marker);

        Assert.StartsWith("Add a /health endpoint.", brief);
        Assert.Contains("/repo/.orchestrator/worktrees/t1", brief);
        Assert.Contains("dotnet test", brief);
        Assert.Contains(Marker, brief);
    }
}
