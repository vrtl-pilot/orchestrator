using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Model;

namespace Orchestrator.Tests;

/// <summary>Platforms added from the Setup page / config.json, without code.</summary>
public class ConfiguredAgentTests
{
    private static AgentTurnContext Turn(AgentTask task, AgentOptions options, string message = "do it", string? resume = null) =>
        new() { Task = task, Message = message, ResumeSessionId = resume, ScratchDirectory = "/tmp", Options = options };

    [Fact]
    public void Arguments_are_templated_with_model_session_and_prompt()
    {
        var options = new AgentOptions
        {
            Executable = "gemini",
            Protocol = ConfiguredAgentAdapter.ClaudeStreamJson,
            Args = ["-p", "--output-format", "stream-json"],
            ModelArgs = ["--model", "{model}"],
            ResumeArgs = ["--resume", "{sessionId}"],
            ExtraArgs = ["--yolo"],
        };
        var adapter = new ConfiguredAgentAdapter("gemini", options);
        var task = new AgentTask { Id = "t", Agent = "gemini", Prompt = "p", RepoRoot = "/r", Model = "pro" };

        var first = adapter.BuildInvocation(Turn(task, options));
        Assert.Equal("gemini", first.Executable);
        Assert.Equal(["-p", "--output-format", "stream-json", "--model", "pro", "--yolo"], first.Arguments);
        Assert.Equal("do it", first.StandardInput);

        task.Turns = 1;
        var resumed = adapter.BuildInvocation(Turn(task, options, "answer", "s-1"));
        Assert.Equal(["-p", "--output-format", "stream-json", "--model", "pro", "--resume", "s-1", "--yolo"], resumed.Arguments);
        Assert.Equal("answer", resumed.StandardInput);
    }

    [Fact]
    public void Prompt_can_go_on_the_command_line()
    {
        var options = new AgentOptions { Protocol = "text", Args = ["run", "--message={prompt}", "--quiet"], PromptVia = "arg" };
        var task = new AgentTask { Id = "t", Agent = "x", Prompt = "p", RepoRoot = "/r" };

        var inv = new ConfiguredAgentAdapter("x", options).BuildInvocation(Turn(task, options));

        Assert.Equal(["run", "--message=do it", "--quiet"], inv.Arguments);
        Assert.Null(inv.StandardInput);
        Assert.Equal("x", inv.Executable);

        var appended = new AgentOptions { Protocol = "text", Args = ["ask"], PromptVia = "arg" };
        Assert.Equal(["ask", "do it"], new ConfiguredAgentAdapter("x", appended).BuildInvocation(Turn(task, appended)).Arguments);
    }

    [Fact]
    public void Without_resume_a_follow_up_restates_the_task()
    {
        var options = new AgentOptions { Protocol = "text" };
        var task = new AgentTask { Id = "t", Agent = "x", Prompt = "Write the parser.", RepoRoot = "/r", Turns = 1, Summary = "Which format?" };

        var inv = new ConfiguredAgentAdapter("x", options).BuildInvocation(Turn(task, options, "Use JSON.", "ignored"));

        Assert.Contains("Write the parser.", inv.StandardInput);
        Assert.Contains("Which format?", inv.StandardInput);
        Assert.Contains("Use JSON.", inv.StandardInput);
    }

    [Fact]
    public void Protocol_selects_the_matching_parser()
    {
        Assert.IsType<ClaudeCodeAdapter.Parser>(new ConfiguredAgentAdapter("a", new AgentOptions { Protocol = "claude-stream-json" }).CreateParser());
        Assert.IsType<CodexAdapter.Parser>(new ConfiguredAgentAdapter("a", new AgentOptions { Protocol = "codex-jsonl" }).CreateParser());
        Assert.IsType<OpenCodeAdapter.Parser>(new ConfiguredAgentAdapter("a", new AgentOptions { Protocol = "opencode-json" }).CreateParser());

        var text = new ConfiguredAgentAdapter("a", new AgentOptions { Protocol = "text" }).CreateParser();
        var events = new[] { "Working…", "", "Done: added tests." }.SelectMany(text.ParseLine).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(AgentEventKind.Message, e.Kind));
        Assert.Equal("Working…\n\nDone: added tests.", text.FinalMessage);
        Assert.Null(text.SessionId);
    }

    [Fact]
    public void Registry_lists_custom_platforms_and_applies_built_in_defaults()
    {
        var options = new OrchestratorOptions
        {
            Agents =
            {
                ["gemini"] = new AgentOptions { Protocol = "text", Executable = "sh", DisplayName = "Gemini CLI" },
                ["noprotocol"] = new AgentOptions { Executable = "sh" },
                ["claude"] = new AgentOptions { Model = "opus" },
            },
        };
        var registry = new AgentRegistry([new ClaudeCodeAdapter(), new CodexAdapter()], Options.Create(options));

        var all = registry.Describe();
        Assert.Equal(["claude", "codex", "gemini"], all.Select(a => a.Name));
        Assert.Equal("Gemini CLI", all.Single(a => a.Name == "gemini").DisplayName);
        Assert.False(all.Single(a => a.Name == "gemini").BuiltIn);
        Assert.Equal("opus", all.Single(a => a.Name == "claude").Model);

        // Config that doesn't set ExtraArgs gets the adapter's permission defaults.
        Assert.Equal(["--permission-mode", "acceptEdits", "--permission-prompts", "none"], registry.OptionsFor("claude").ExtraArgs);
        Assert.Equal(["--sandbox", "workspace-write"], registry.OptionsFor("codex").ExtraArgs);
        Assert.True(registry.TryGet("GEMINI", out var adapter, out _));
        Assert.IsType<ConfiguredAgentAdapter>(adapter);
    }
}
