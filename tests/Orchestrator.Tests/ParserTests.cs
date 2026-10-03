using Orchestrator.Core.Agents;
using Orchestrator.Core.Model;

namespace Orchestrator.Tests;

public class ParserTests
{
    [Fact]
    public void Claude_stream_json_yields_session_tools_and_final_result()
    {
        var parser = new ClaudeCodeAdapter().CreateParser();
        var lines = new[]
        {
            """{"type":"system","subtype":"init","session_id":"s-1","model":"claude-x"}""",
            """{"type":"assistant","session_id":"s-1","message":{"content":[{"type":"text","text":"Looking at the code"},{"type":"tool_use","name":"Bash","input":{"command":"dotnet test","description":"run tests"}}]}}""",
            """{"type":"assistant","session_id":"s-1","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/A.cs"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"boom"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Fixed the bug.","session_id":"s-1"}""",
        };

        var events = lines.SelectMany(parser.ParseLine).ToList();

        Assert.Equal("s-1", parser.SessionId);
        Assert.Equal("Fixed the bug.", parser.FinalMessage);
        Assert.Null(parser.Error);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Command && e.Text == "dotnet test");
        Assert.Contains(events, e => e.Kind == AgentEventKind.FileChange && e.Text == "Edit src/A.cs");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Detail == "boom");
    }

    [Fact]
    public void Claude_error_result_sets_error()
    {
        var parser = new ClaudeCodeAdapter().CreateParser();
        parser.ParseLine("""{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s-2"}""").ToList();
        Assert.Equal("error_max_turns", parser.Error);
    }

    [Fact]
    public void Codex_jsonl_yields_thread_items_and_last_agent_message()
    {
        var parser = new CodexAdapter().CreateParser();
        var lines = new[]
        {
            """{"type":"thread.started","thread_id":"th-9"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"npm test","aggregated_output":"","status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"npm test","aggregated_output":"ok","exit_code":0,"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"i2","type":"file_change","changes":[{"path":"a.ts","kind":"update"}],"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"i3","type":"agent_message","text":"Done: updated a.ts"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1,"reasoning_output_tokens":0}}""",
        };

        var events = lines.SelectMany(parser.ParseLine).ToList();

        Assert.Equal("th-9", parser.SessionId);
        Assert.Equal("Done: updated a.ts", parser.FinalMessage);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Command && e.Text == "npm test" && e.Detail!.Contains("exit=0"));
        Assert.Contains(events, e => e.Kind == AgentEventKind.FileChange && e.Text == "update a.ts");
    }

    [Fact]
    public void Codex_turn_failure_sets_error()
    {
        var parser = new CodexAdapter().CreateParser();
        parser.ParseLine("""{"type":"turn.failed","error":{"message":"rate limited"}}""").ToList();
        Assert.Equal("rate limited", parser.Error);
    }

    [Fact]
    public void OpenCode_json_events_yield_session_tools_and_text()
    {
        var parser = new OpenCodeAdapter().CreateParser();
        var lines = new[]
        {
            """{"type":"step_start","timestamp":1,"sessionID":"ses_1","part":{"type":"step-start"}}""",
            """{"type":"tool_use","timestamp":2,"sessionID":"ses_1","part":{"type":"tool","tool":"bash","state":{"status":"completed","input":{"command":"go test ./..."},"output":"PASS"}}}""",
            """{"type":"tool_use","timestamp":3,"sessionID":"ses_1","part":{"type":"tool","tool":"edit","state":{"status":"completed","input":{"filePath":"main.go"},"title":"main.go"}}}""",
            """{"type":"text","timestamp":4,"sessionID":"ses_1","part":{"type":"text","text":"All tests pass."}}""",
            "not json at all",
        };

        var events = lines.SelectMany(parser.ParseLine).ToList();

        Assert.Equal("ses_1", parser.SessionId);
        Assert.Equal("All tests pass.", parser.FinalMessage);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Command && e.Text == "go test ./..." && e.Detail == "PASS");
        Assert.Contains(events, e => e.Kind == AgentEventKind.FileChange && e.Text == "edit main.go");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Log && e.Text == "not json at all");
    }

    [Fact]
    public void OpenCode_error_event_sets_error()
    {
        var parser = new OpenCodeAdapter().CreateParser();
        parser.ParseLine("""{"type":"error","sessionID":"ses_1","error":{"name":"ProviderAuthError","data":{"message":"no credentials"}}}""").ToList();
        Assert.Equal("no credentials", parser.Error);
    }

    [Fact]
    public void OpenCode_windows_shell_tool_and_edit_without_filePath_are_labelled_correctly()
    {
        // Shapes seen on Windows with OpenCode 1.18: the shell tool is "shell", and the edit event's
        // title repeats the tool name while the path is only in the output.
        var parser = new OpenCodeAdapter().CreateParser();
        var shell = parser.ParseLine("""{"type":"tool_use","sessionID":"s","part":{"type":"tool","tool":"shell","state":{"status":"completed","title":"shell","input":{"command":"Select-String Contributing README.md"},"output":"README.md:12:## Contributing"}}}""").Single();
        var edit = parser.ParseLine("""{"type":"tool_use","sessionID":"s","part":{"type":"tool","tool":"edit","state":{"status":"completed","title":"edit","input":{},"output":"Edited README.md (1 replacement)"}}}""").Single();
        var write = parser.ParseLine("""{"type":"tool_use","sessionID":"s","part":{"type":"tool","tool":"write","state":{"status":"completed","input":{"path":"docs/a.md"}}}}""").Single();

        Assert.Equal((AgentEventKind.Command, "Select-String Contributing README.md"), (shell.Kind, shell.Text));
        Assert.Equal((AgentEventKind.FileChange, "edit Edited README.md (1 replacement)"), (edit.Kind, edit.Text));
        Assert.Equal((AgentEventKind.FileChange, "write docs/a.md"), (write.Kind, write.Text));
    }
}
