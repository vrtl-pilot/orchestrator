using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orchestrator.Tests;

[CollectionDefinition(nameof(ServerEnvironment), DisableParallelization = true)]
public sealed class ServerEnvironment;

/// <summary>
/// The HTTP server in-process with a throwaway per-user folder (<c>ORCHESTRATOR_HOME</c>): request filtering, the
/// Setup API writing config.json, and a custom platform being connected by its configured commands.
/// </summary>
[Collection(nameof(ServerEnvironment))]
public sealed class ApiTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("orch-home-").FullName;
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("ORCHESTRATOR_HOME");
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        Environment.SetEnvironmentVariable("ORCHESTRATOR_HOME", _home);
        File.WriteAllText(Path.Combine(_home, "config.json"), """{ "Orchestrator": { "MaxDepth": 5, "Agents": { "codex": { "Model": "o3" } } } }""");
        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ORCHESTRATOR_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private HttpClient Client(bool withClientHeader = true)
    {
        var client = _factory.CreateClient();
        if (withClientHeader) client.DefaultRequestHeaders.Add("X-Orchestrator-Client", "tests");
        return client;
    }

    [Fact]
    public async Task Browser_style_cross_site_requests_are_refused()
    {
        var bare = Client(withClientHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.PostAsync("/api/setup/platforms/claude/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/agents")).StatusCode);

        var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        foreignOrigin.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(foreignOrigin)).StatusCode);

        var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        sameOrigin.Headers.Add("Origin", "http://127.0.0.1:7777");
        Assert.Equal(HttpStatusCode.OK, (await bare.SendAsync(sameOrigin)).StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        rebinding.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.SendAsync(rebinding)).StatusCode);
    }

    [Fact]
    public async Task User_config_file_overrides_shipped_settings()
    {
        var agents = await Client().GetFromJsonAsync<JsonArray>("/api/agents");
        Assert.Equal("o3", agents!.Single(a => a!["name"]!.GetValue<string>() == "codex")!["model"]!.GetValue<string>());

        var info = await Client().GetFromJsonAsync<JsonObject>("/api/setup/info");
        Assert.Equal(Path.Combine(_home, "config.json"), info!["configFile"]!.GetValue<string>());
        Assert.EndsWith("/#setup", info["urls"]!["setup"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delegating_without_a_platform_returns_the_choices_and_starts_nothing()
    {
        var response = await Client().PostAsJsonAsync("/api/tasks", new { prompt = "x", repoPath = _home });
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("agent_selection_required", body!["status"]!.GetValue<string>());
        Assert.Contains("Ask the user", body["instruction"]!.GetValue<string>());
        Assert.Empty((await Client().GetFromJsonAsync<JsonArray>("/api/tasks"))!);
    }

    [Fact]
    public async Task Custom_platform_can_be_added_edited_connected_and_removed()
    {
        if (OperatingSystem.IsWindows()) return; // The registration command below is a POSIX shell one-liner.

        var marker = Path.Combine(_home, "registered.txt");
        var skills = Path.Combine(_home, "skills");
        var client = Client();

        var created = await client.PostAsJsonAsync("/api/setup/platforms", new
        {
            name = "echoer",
            displayName = "Echo agent",
            executable = "sh",
            protocol = "text",
            args = new[] { "-c", "cat" },
            integration = new
            {
                mcpAdd = new[] { "sh", "-c", $"echo {{url}} > '{marker}'" },
                mcpRemove = new[] { "sh", "-c", $"rm -f '{marker}'" },
                skillsDirs = new[] { skills },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, "config.json")))!;
        Assert.Equal("text", config["Orchestrator"]!["Agents"]!["echoer"]!["Protocol"]!.GetValue<string>());
        Assert.Equal(5, config["Orchestrator"]!["MaxDepth"]!.GetValue<int>());
        Assert.Contains(await client.GetFromJsonAsync<JsonArray>("/api/agents") ?? [], a => a!["name"]!.GetValue<string>() == "echoer");

        var edited = await (await client.PutAsJsonAsync("/api/setup/platforms/echoer", new { model = "m1" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("m1", edited!["model"]!.GetValue<string>());

        var connect = await (await client.PostAsync("/api/setup/platforms/echoer/connect", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(connect!["ok"]!.GetValue<bool>(), connect.ToJsonString());
        Assert.True(connect["platform"]!["connected"]!.GetValue<bool>());
        Assert.EndsWith("/mcp", File.ReadAllText(marker).Trim());
        Assert.True(File.Exists(Path.Combine(skills, "agent-orchestrator", "SKILL.md")));

        var check = await (await client.PostAsync("/api/setup/platforms/echoer/check", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(check!["steps"]);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/setup/platforms/echoer")).StatusCode);
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(Path.Combine(skills, "agent-orchestrator")));
        Assert.DoesNotContain(await client.GetFromJsonAsync<JsonArray>("/api/agents") ?? [], a => a!["name"]!.GetValue<string>() == "echoer");
    }

    [Fact]
    public async Task Invalid_custom_platforms_are_rejected()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/setup/platforms", new { name = "claude", executable = "x", protocol = "text" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/setup/platforms", new { name = "Bad Name", executable = "x", protocol = "text" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/setup/platforms", new { name = "ok", executable = "x", protocol = "xml" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/setup/platforms/claude")).StatusCode);
    }

    [Fact]
    public async Task Allowed_commands_are_saved_to_the_settings_file()
    {
        var client = Client();
        Assert.Equal(["dotnet"], (await client.GetFromJsonAsync<JsonObject>("/api/setup/info"))!["allowedCommands"]!.AsArray().Select(n => n!.GetValue<string>()));

        var saved = await (await client.PutAsJsonAsync("/api/setup/settings", new { allowedCommands = new[] { "dotnet", "npm test" } })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(["dotnet", "npm test"], saved!["allowedCommands"]!.AsArray().Select(n => n!.GetValue<string>()));
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, "config.json")))!;
        Assert.Equal(5, config["Orchestrator"]!["MaxDepth"]!.GetValue<int>());
        Assert.Equal(2, config["Orchestrator"]!["AllowedCommands"]!.AsArray().Count);

        var cleared = await (await client.PutAsJsonAsync("/api/setup/settings", new { allowedCommands = Array.Empty<string>() })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Empty(cleared!["allowedCommands"]!.AsArray());
    }
}
