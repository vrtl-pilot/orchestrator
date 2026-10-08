using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Setup;

namespace Orchestrator.Api;

/// <summary>
/// <c>/api/setup/*</c>: what the dashboard's Setup page (and <c>orch setup</c>) use to see and connect platforms, add
/// custom ones, and switch autostart. Settings are written to the user's config.json and applied without a restart.
/// </summary>
public static partial class SetupEndpoints
{
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Version =>
        typeof(SetupEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void MapSetup(this IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup");

        setup.MapGet("/info", async (IOptionsMonitor<OrchestratorOptions> monitor, UserConfigStore config, CancellationToken ct) =>
        {
            var o = monitor.CurrentValue;
            var url = (o.PublicUrl ?? "http://127.0.0.1:7777").TrimEnd('/');
            return new
            {
                Version,
                Urls = new { Dashboard = url + "/", Setup = url + "/#setup", Task = url + "/#task=<task-id>", Mcp = url + "/mcp" },
                o.DataDirectory,
                ConfigFile = config.Path,
                LogFile = ResolveLogFile(o),
                Installed = Autostart.InstalledServer() is not null,
                ServerExecutable = Environment.ProcessPath,
                Autostart = await Autostart.StatusAsync(ct),
                ApiKeyConfigured = !string.IsNullOrEmpty(o.ApiKey),
                AllowedCommands = o.EffectiveAllowedCommands,
            };
        });

        // Settings that apply to every platform.
        setup.MapPut("/settings", (SettingsRequest body, UserConfigStore config, IConfiguration configuration,
            IOptionsMonitor<OrchestratorOptions> monitor) =>
        {
            if (body.AllowedCommands is { } commands)
            {
                var clean = commands.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();
                // An empty JSON array never reaches the options binder; [""] means "none".
                config.UpdateOrchestrator(o => o["AllowedCommands"] = new JsonArray([.. (clean.Count > 0 ? clean : [""]).Select(c => JsonValue.Create(c))]));
            }
            Reload(configuration);
            return new { AllowedCommands = monitor.CurrentValue.EffectiveAllowedCommands };
        });

        setup.MapGet("/platforms", (PlatformSetupService service) => service.List());
        setup.MapGet("/platforms/{name}", (string name, PlatformSetupService service) => service.Get(name));

        setup.MapPost("/platforms/{name}/connect", (string name, PlatformSetupService service, CancellationToken ct) =>
            service.ConnectAsync(name, ct));
        setup.MapPost("/platforms/{name}/disconnect", (string name, PlatformSetupService service, CancellationToken ct) =>
            service.DisconnectAsync(name, ct));
        setup.MapPost("/platforms/{name}/check", (string name, PlatformSetupService service, CancellationToken ct) =>
            service.CheckAsync(name, ct));

        // Edit settings of any platform (built-in or custom).
        setup.MapPut("/platforms/{name}", (string name, UpdatePlatformRequest body, PlatformSetupService service,
            UserConfigStore config, IConfiguration configuration) =>
        {
            var current = service.Get(name);
            config.UpdateAgent(current.Name, agent =>
            {
                if (body.Enabled is { } enabled) agent["Enabled"] = enabled;
                if (body.Model is not null) SetOrRemove(agent, "Model", body.Model);
                if (body.Executable is not null) SetOrRemove(agent, "Executable", body.Executable);
                if (body.ResetExtraArgs == true) agent.Remove("ExtraArgs");
                else if (body.ExtraArgs is not null) agent["ExtraArgs"] = new JsonArray([.. body.ExtraArgs.Select(a => JsonValue.Create(a))]);
            });
            Reload(configuration);
            return service.Get(current.Name);
        });

        // Add (or replace) a custom platform.
        setup.MapPost("/platforms", (CustomPlatformRequest body, PlatformSetupService service, AgentRegistry registry,
            UserConfigStore config, IConfiguration configuration) =>
        {
            var name = (body.Name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern().IsMatch(name))
                throw new OrchestratorException("Name must be 1-32 characters: lowercase letters, digits and '-', starting with a letter or digit.");
            if (registry.IsBuiltIn(name))
                throw new OrchestratorException($"'{name}' is a built-in platform; edit it instead of adding it.");
            if (string.IsNullOrWhiteSpace(body.Executable))
                throw new OrchestratorException("Executable is required (program name on PATH or a full path).");
            if (!ConfiguredAgentAdapter.Protocols.Contains(body.Protocol ?? ""))
                throw new OrchestratorException($"Protocol must be one of: {string.Join(", ", ConfiguredAgentAdapter.Protocols)}.");
            if (body.PromptVia is not (null or "stdin" or "arg"))
                throw new OrchestratorException("PromptVia must be 'stdin' or 'arg'.");

            var options = new AgentOptions
            {
                DisplayName = Blank(body.DisplayName),
                Executable = body.Executable.Trim(),
                Protocol = body.Protocol,
                Args = Clean(body.Args),
                ModelArgs = Clean(body.ModelArgs),
                ResumeArgs = Clean(body.ResumeArgs),
                ExtraArgs = Clean(body.ExtraArgs),
                PromptVia = body.PromptVia,
                Model = Blank(body.Model),
                VersionArgs = Clean(body.VersionArgs),
                InstallHint = Blank(body.InstallHint),
                Integration = body.Integration is { } i && (Clean(i.McpAdd) ?? Clean(i.McpRemove) ?? Clean(i.SkillsDirs)) is not null
                    ? new IntegrationOptions { McpAdd = Clean(i.McpAdd), McpRemove = Clean(i.McpRemove), SkillsDirs = Clean(i.SkillsDirs) }
                    : null,
            };
            var node = JsonSerializer.SerializeToNode(options, ConfigJson)!.AsObject();
            node.Remove("Environment");
            node.Remove("MaxConcurrent");
            config.UpdateAgent(name, agent =>
            {
                foreach (var key in agent.Select(kv => kv.Key).ToList()) agent.Remove(key);
                foreach (var (key, value) in node.ToList())
                {
                    node.Remove(key);
                    agent[key] = value;
                }
            });
            Reload(configuration);
            return Results.Created($"/api/setup/platforms/{name}", service.Get(name));
        });

        setup.MapDelete("/platforms/{name}", async (string name, PlatformSetupService service, AgentRegistry registry,
            UserConfigStore config, IConfiguration configuration, CancellationToken ct) =>
        {
            if (registry.IsBuiltIn(name))
                throw new OrchestratorException($"'{name}' is built in and cannot be removed. Disable it instead.");
            var result = await service.DisconnectAsync(name, ct);
            config.RemoveAgent(name);
            Reload(configuration);
            return result with { Platform = null };
        });

        setup.MapPost("/autostart", async (AutostartRequest body, CancellationToken ct) =>
            body.Enabled ? await Autostart.EnableAsync(ct: ct) : await Autostart.DisableAsync(ct));
    }

    public static string? ResolveLogFile(OrchestratorOptions o) =>
        o.LogFile.Equals("off", StringComparison.OrdinalIgnoreCase) ? null
        : string.IsNullOrWhiteSpace(o.LogFile) ? Path.Combine(o.DataDirectory, "logs", "server.log")
        : Path.GetFullPath(OrchestratorPaths.ExpandHome(o.LogFile));

    /// <summary>Applies config.json edits immediately instead of waiting for the file watcher.</summary>
    private static void Reload(IConfiguration configuration) => (configuration as IConfigurationRoot)?.Reload();

    private static void SetOrRemove(JsonObject agent, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) agent.Remove(key);
        else agent[key] = value.Trim();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string>? Clean(List<string>? list) =>
        list?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() is { Count: > 0 } l ? l : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}

public sealed record UpdatePlatformRequest(bool? Enabled, string? Model, string? Executable, List<string>? ExtraArgs, bool? ResetExtraArgs);

public sealed record CustomPlatformRequest(
    string? Name,
    string? DisplayName,
    string? Executable,
    string? Protocol,
    List<string>? Args,
    List<string>? ModelArgs,
    List<string>? ResumeArgs,
    List<string>? ExtraArgs,
    string? PromptVia,
    string? Model,
    List<string>? VersionArgs,
    string? InstallHint,
    IntegrationOptions? Integration);

public sealed record AutostartRequest(bool Enabled);

public sealed record SettingsRequest(List<string>? AllowedCommands);
