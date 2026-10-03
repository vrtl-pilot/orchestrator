using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// `orch` — command-line front door to the orchestrator HTTP API.
// Any agent with a shell (Claude Code's Bash tool, Codex, OpenCode) or a human can use it.
//
// Exit codes for `delegate --wait` and `wait`: 0 completed, 2 input required, 1 failed/cancelled/error, 3 still running.

var exitCode = await Cli.RunAsync(args);
return exitCode;

internal static class Cli
{
    private const string Usage = """
        orch — delegate coding tasks to Claude Code, Codex or OpenCode via the Agent Orchestrator

        Usage:
          orch delegate <agent> [prompt...] [options]   Start a task (prompt from args, --prompt-file, or stdin)
              --repo PATH         Repository (default: current directory)
              --base REF          Branch from REF instead of HEAD + uncommitted changes
              --no-uncommitted    Do not include uncommitted changes in the base
              --model M           Model override
              --test "CMD"        Test command run in the worktree afterwards
              --timeout MIN       Time limit in minutes
              --parent ID         Parent task id (default: $ORCHESTRATOR_TASK_ID)
              --id KEY            Idempotency key
              --prompt-file F     Read the prompt from a file
              --wait              Wait until the task settles (see exit codes)
              --wait-timeout SEC  Give up waiting after SEC seconds (exit 3; the task keeps running)
              --watch             Stream live activity until the task settles
          orch wait <id> [--timeout SEC]   Wait until completed / failed / input required (default: no limit)
          orch watch <id>                  Stream the agent's live activity in this terminal
          orch get <id>                    Show task state (JSON)
          orch list [--status S] [--limit N]
          orch answer <id> <text...>       Answer the agent's pending question (resumes it)
          orch continue <id> <text...>     Send a follow-up to a finished task's agent session
          orch cancel <id>
          orch cleanup <id> [--delete-branch] [--force]   Remove the worktree; branch deleted only if merged (or --force)
          orch agents                      Show available agents

        Environment: ORCHESTRATOR_URL (default http://127.0.0.1:7777), ORCHESTRATOR_API_KEY
        Exit codes (wait): 0 completed, 2 input required, 1 failed/cancelled, 3 still running
        """;

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var (positional, flags) = Parse(args.Skip(1));
        var baseUrl = (flags.GetValueOrDefault("url") ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_URL") ?? "http://127.0.0.1:7777").TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = Timeout.InfiniteTimeSpan };
        if (Environment.GetEnvironmentVariable("ORCHESTRATOR_API_KEY") is { Length: > 0 } key)
        {
            http.DefaultRequestHeaders.Add("X-Orchestrator-Key", key);
        }

        try
        {
            return args[0] switch
            {
                "delegate" => await DelegateAsync(http, positional, flags),
                "wait" => await WaitAsync(http, Require(positional, 0, "task id"), flags),
                "watch" => await WatchAsync(http, Require(positional, 0, "task id")),
                "get" => await PrintAsync(http.GetAsync($"api/tasks/{Require(positional, 0, "task id")}")),
                "list" => await PrintAsync(http.GetAsync($"api/tasks?limit={flags.GetValueOrDefault("limit") ?? "20"}{(flags.TryGetValue("status", out var s) ? $"&status={s}" : "")}")),
                "answer" => await PrintAsync(http.PostAsJsonAsync($"api/tasks/{Require(positional, 0, "task id")}/answer", new { message = Text(positional, flags) })),
                "continue" => await PrintAsync(http.PostAsJsonAsync($"api/tasks/{Require(positional, 0, "task id")}/continue", new { message = Text(positional, flags) })),
                "cancel" => await PrintAsync(http.PostAsync($"api/tasks/{Require(positional, 0, "task id")}/cancel", null)),
                "cleanup" => await PrintAsync(http.DeleteAsync($"api/tasks/{Require(positional, 0, "task id")}/worktree?deleteBranch={flags.ContainsKey("delete-branch")}&force={flags.ContainsKey("force")}")),
                "agents" => await PrintAsync(http.GetAsync("api/agents")),
                _ => throw new UsageException($"Unknown command '{args[0]}'. Run `orch --help`."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Cannot reach the orchestrator at {baseUrl}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DelegateAsync(HttpClient http, List<string> positional, Dictionary<string, string?> flags)
    {
        var agent = Require(positional, 0, "agent (claude, codex or opencode)");
        string prompt;
        if (flags.GetValueOrDefault("prompt-file") is { } file) prompt = await File.ReadAllTextAsync(file);
        else if (positional.Count > 1) prompt = string.Join(' ', positional.Skip(1));
        else if (Console.IsInputRedirected) prompt = await Console.In.ReadToEndAsync();
        else throw new UsageException("A prompt is required (arguments, --prompt-file, or stdin).");

        var request = new Dictionary<string, object?>
        {
            ["agent"] = agent,
            ["prompt"] = prompt,
            ["repoPath"] = Path.GetFullPath(flags.GetValueOrDefault("repo") ?? Directory.GetCurrentDirectory()),
            ["baseRef"] = flags.GetValueOrDefault("base"),
            ["includeUncommitted"] = !flags.ContainsKey("no-uncommitted"),
            ["model"] = flags.GetValueOrDefault("model"),
            ["testCommand"] = flags.GetValueOrDefault("test"),
            ["timeoutMinutes"] = int.TryParse(flags.GetValueOrDefault("timeout"), out var t) ? t : null,
            ["parentTaskId"] = flags.GetValueOrDefault("parent") ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_TASK_ID"),
            ["clientRequestId"] = flags.GetValueOrDefault("id"),
        };

        var response = await http.PostAsJsonAsync("api/tasks", request);
        var body = await ReadAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(Error(body));
            return 1;
        }

        var id = body!["id"]!.GetValue<string>();
        if (flags.ContainsKey("watch"))
        {
            Console.Error.WriteLine($"task {id} started — streaming live activity (Ctrl+C stops watching, not the task)");
            await WatchAsync(http, id);
            return await WaitAsync(http, id, WaitFlags(flags));
        }
        if (flags.ContainsKey("wait")) return await WaitAsync(http, id, WaitFlags(flags));

        Console.WriteLine(body.ToJsonString(Pretty));
        return 0;
    }

    /// <summary>For `delegate --wait`, --timeout is the task's limit in minutes; the wait limit is --wait-timeout (seconds).</summary>
    private static Dictionary<string, string?> WaitFlags(Dictionary<string, string?> flags) =>
        flags.TryGetValue("wait-timeout", out var seconds)
            ? new Dictionary<string, string?> { ["timeout"] = seconds }
            : [];

    private static async Task<int> WaitAsync(HttpClient http, string id, Dictionary<string, string?> flags)
    {
        var limit = int.TryParse(flags.GetValueOrDefault("timeout"), out var seconds) && seconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : DateTimeOffset.MaxValue;

        while (true)
        {
            var remaining = limit == DateTimeOffset.MaxValue ? 60 : (int)Math.Clamp((limit - DateTimeOffset.UtcNow).TotalSeconds, 1, 60);
            var response = await http.GetAsync($"api/tasks/{id}/wait?timeoutSeconds={remaining}");
            var body = await ReadAsync(response);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(Error(body));
                return 1;
            }

            var status = body!["status"]?.GetValue<string>();
            var settled = status is "completed" or "failed" or "cancelled" or "input_required";
            if (settled || DateTimeOffset.UtcNow >= limit)
            {
                Console.WriteLine(body.ToJsonString(Pretty));
                if (status == "completed" && body["testsPassed"]?.GetValue<bool>() == false)
                {
                    Console.Error.WriteLine($"warning: task {id} completed but its test command FAILED (exit {body["testExitCode"]}). Review testOutputTail before merging.");
                }
                return status switch
                {
                    "completed" => 0,
                    "input_required" => 2,
                    "failed" or "cancelled" => 1,
                    _ => 3,
                };
            }
        }
    }

    /// <summary>Prints the task's live event stream (SSE) until it completes, fails, or needs input.</summary>
    private static async Task<int> WatchAsync(HttpClient http, string id)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/tasks/{id}/events");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(Error(await ReadAsync(response)));
                return 1;
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var evt = JsonNode.Parse(line[5..].Trim());
                if (evt is null) continue;
                var kind = evt["kind"]?.GetValue<string>() ?? "";
                var text = evt["text"]?.GetValue<string>() ?? "";
                var ts = DateTimeOffset.TryParse(evt["timestamp"]?.GetValue<string>(), out var parsed) ? parsed.ToLocalTime().ToString("HH:mm:ss") : "";
                WriteEvent(ts, kind, text, evt["detail"]?.GetValue<string>());

                // The stream replays history first, so a settled status may be an old one (e.g. a question that
                // has since been answered). Stop only if the task is settled right now.
                if (kind == "status" && text is "Completed" or "Failed" or "Cancelled" or "InputRequired"
                    && await IsSettledAsync(http, id, cts.Token))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static async Task<bool> IsSettledAsync(HttpClient http, string id, CancellationToken ct)
    {
        var body = await ReadAsync(await http.GetAsync($"api/tasks/{id}", ct));
        return body?["status"]?.GetValue<string>() is "completed" or "failed" or "cancelled" or "input_required";
    }

    private static void WriteEvent(string ts, string kind, string text, string? detail)
    {
        var color = kind switch
        {
            "status" => ConsoleColor.Cyan,
            "message" => ConsoleColor.White,
            "command" or "file_change" => ConsoleColor.Yellow,
            "error" => ConsoleColor.Red,
            "test" => ConsoleColor.Green,
            _ => ConsoleColor.DarkGray,
        };
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{ts} {kind,-11} ");
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        if (kind is "status" && text == "InputRequired" && detail is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"           question: {detail}");
        }
        Console.ForegroundColor = previous;
    }

    private static async Task<int> PrintAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var body = await ReadAsync(response);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(Error(body));
            return 1;
        }
        Console.WriteLine(body?.ToJsonString(Pretty) ?? "");
        return 0;
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static string Error(JsonNode? body) =>
        body is JsonObject o && o["error"] is { } e ? $"error: {e}" : $"error: {body?.ToJsonString() ?? "request failed"}";

    private static string Text(List<string> positional, Dictionary<string, string?> flags)
    {
        if (positional.Count > 1) return string.Join(' ', positional.Skip(1));
        if (Console.IsInputRedirected) return Console.In.ReadToEnd();
        throw new UsageException("Message text is required.");
    }

    private static string Require(List<string> positional, int index, string what) =>
        positional.Count > index ? positional[index] : throw new UsageException($"Missing {what}. Run `orch --help`.");

    private static readonly HashSet<string> BooleanFlags = ["wait", "watch", "no-uncommitted", "delete-branch", "force"];

    private static (List<string> Positional, Dictionary<string, string?> Flags) Parse(IEnumerable<string> args)
    {
        var positional = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var e = args.GetEnumerator();
        var literal = false;
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (literal || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                literal = true;
                continue;
            }
            var name = arg[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0) flags[name[..eq]] = name[(eq + 1)..];
            else if (BooleanFlags.Contains(name)) flags[name] = null;
            else flags[name] = e.MoveNext() ? e.Current : throw new UsageException($"--{name} needs a value.");
        }
        return (positional, flags);
    }

    private sealed class UsageException(string message) : Exception(message);
}
