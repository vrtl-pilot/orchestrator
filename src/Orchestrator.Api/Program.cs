using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Api;
using Orchestrator.Core;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Git;
using Orchestrator.Core.Model;
using Orchestrator.Core.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.SectionName));
builder.Services.PostConfigure<OrchestratorOptions>(o => o.DataDirectory = Path.GetFullPath(o.DataDirectory));

builder.Services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();
builder.Services.AddSingleton<IAgentAdapter, CodexAdapter>();
builder.Services.AddSingleton<IAgentAdapter, OpenCodeAdapter>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<ITaskStore>(sp =>
    new SqliteTaskStore(Path.Combine(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.DataDirectory, "orchestrator.db")));
builder.Services.AddSingleton(sp => new TaskEventLog(sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.DataDirectory));
builder.Services.AddSingleton<TaskCoordination>();
builder.Services.AddSingleton<WorktreeManager>();
builder.Services.AddSingleton<TaskService>();
builder.Services.AddHostedService<TaskRunner>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in OrchestratorJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
    o.SerializerOptions.DefaultIgnoreCondition = OrchestratorJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.Encoder = OrchestratorJson.Options.Encoder;
});

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "agent-orchestrator", Version = "0.1.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<OrchestratorTools>();

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<OrchestratorOptions>>().Value;

app.Lifetime.ApplicationStarted.Register(() =>
{
    options.PublicUrl ??= app.Urls.FirstOrDefault()?.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1");
    app.Logger.LogInformation("Orchestrator ready. Dashboard: {Url}  MCP: {Url}/mcp  Data: {Data}",
        options.PublicUrl, options.PublicUrl, options.DataDirectory);
});

// Optional shared-secret protection for the API and MCP endpoints: X-Orchestrator-Key header,
// Authorization: Bearer (what Codex's --bearer-token-env-var sends), or ?key= (EventSource cannot set headers).
if (!string.IsNullOrEmpty(options.ApiKey))
{
    var expected = Encoding.UTF8.GetBytes(options.ApiKey);
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp"))
        {
            var bearer = context.Request.Headers.Authorization.FirstOrDefault();
            var provided = context.Request.Headers["X-Orchestrator-Key"].FirstOrDefault()
                           ?? (bearer?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? bearer[7..].Trim() : null)
                           ?? context.Request.Query["key"].FirstOrDefault()
                           ?? string.Empty;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await next();
    });
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (TaskNotFoundException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (OrchestratorException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

var api = app.MapGroup("/api");

api.MapGet("/agents", (TaskService tasks) => tasks.DescribeAgents());

api.MapPost("/tasks", async (DelegateRequest request, TaskService tasks, CancellationToken ct) =>
{
    var task = await tasks.DelegateAsync(request, ct);
    return Results.Accepted($"/api/tasks/{task.Id}", TaskView.From(task, options.PublicUrl));
});

api.MapGet("/tasks", async (string? status, string? parent, int? limit, TaskService tasks, CancellationToken ct) =>
{
    AgentTaskStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        filter = Enum.TryParse<AgentTaskStatus>(status.Replace("_", ""), true, out var parsed)
            ? parsed
            : throw new OrchestratorException($"Unknown status '{status}'.");
    }
    var list = await tasks.ListAsync(filter, parent, limit ?? 50, ct);
    return list.Select(t => TaskView.From(t, options.PublicUrl, compact: true));
});

api.MapGet("/tasks/{id}", async (string id, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.GetAsync(id, ct) ?? throw new TaskNotFoundException(id), options.PublicUrl));

api.MapGet("/tasks/{id}/wait", async (string id, int? timeoutSeconds, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.WaitAsync(id, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 60, 1, 300)), ct), options.PublicUrl));

api.MapPost("/tasks/{id}/answer", async (string id, MessageBody body, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.AnswerAsync(id, body.Message, ct), options.PublicUrl));

api.MapPost("/tasks/{id}/continue", async (string id, MessageBody body, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.ContinueAsync(id, body.Message, ct), options.PublicUrl));

api.MapPost("/tasks/{id}/cancel", async (string id, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.CancelAsync(id, ct), options.PublicUrl));

api.MapDelete("/tasks/{id}/worktree", async (string id, bool? deleteBranch, bool? force, TaskService tasks, CancellationToken ct) =>
    TaskView.From(await tasks.CleanupAsync(id, deleteBranch ?? false, force ?? false, ct), options.PublicUrl));

api.MapGet("/tasks/{id}/patch", async (string id, TaskService tasks, CancellationToken ct) =>
{
    var task = await tasks.GetAsync(id, ct) ?? throw new TaskNotFoundException(id);
    return task.PatchPath is not null && File.Exists(task.PatchPath)
        ? Results.File(task.PatchPath, "text/x-diff", $"{id}.patch")
        : Results.NotFound(new { error = "No patch recorded for this task yet." });
});

// Live view: replays the task's normalized event log, then streams new events (Server-Sent Events).
api.MapGet("/tasks/{id}/events", async (string id, long? after, bool? follow, TaskService tasks, CancellationToken ct) =>
{
    _ = await tasks.GetAsync(id, ct) ?? throw new TaskNotFoundException(id);
    if (follow == false) return Results.Ok(tasks.GetEvents(id, after ?? 0, 1000));
    return TypedResults.ServerSentEvents(Serialize(tasks.FollowEventsAsync(id, after ?? 0, ct)), eventType: "agent-event");
});

app.Run();

static async IAsyncEnumerable<string> Serialize(IAsyncEnumerable<AgentEvent> source, [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var evt in source.WithCancellation(ct))
    {
        yield return JsonSerializer.Serialize(evt, OrchestratorJson.Options);
    }
}

internal sealed record MessageBody(string Message);

public partial class Program;
