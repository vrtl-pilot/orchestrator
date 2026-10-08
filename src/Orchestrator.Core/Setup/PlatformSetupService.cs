using System.Text;
using System.Text.Json;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Setup;

public sealed record SetupStep(string Step, bool Ok, string? Detail = null);

public sealed record SetupResult(bool Ok, IReadOnlyList<SetupStep> Steps, PlatformStatus? Platform = null);

/// <summary>Everything the Setup page and <c>orch setup</c> show for one platform.</summary>
public sealed record PlatformStatus(
    string Name,
    string DisplayName,
    bool BuiltIn,
    bool Enabled,
    bool Available,
    string Executable,
    string? ResolvedPath,
    string? InstallHint,
    string? Model,
    IReadOnlyList<string> ExtraArgs,
    string? Protocol,
    bool CanConnect,
    bool? McpRegistered,
    bool SkillInstalled,
    IReadOnlyList<string> SkillFiles)
{
    /// <summary>
    /// True when the agent can call the orchestrator (MCP registered; null = can't be detected, e.g. registered by
    /// hand for a custom platform) and knows how (skill installed).
    /// </summary>
    public bool Connected => McpRegistered != false && SkillInstalled;
}

/// <summary>
/// Connects platforms to the orchestrator: registers its MCP endpoint with each agent and installs the skill. Shared by
/// the server's Setup API (dashboard) and the <c>orch setup</c> wizard.
/// </summary>
public sealed class PlatformSetupService(
    AgentRegistry registry,
    Func<OrchestratorOptions> options,
    Func<string> mcpUrl,
    string? stateFile = null)
{
    private readonly string _stateFile = stateFile ?? Path.Combine(OrchestratorPaths.Root, "integrations.json");

    public IReadOnlyList<PlatformStatus> List() => registry.Describe().Select(a => Status(a)).ToList();

    public PlatformStatus Get(string name) =>
        registry.Describe().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } info
            ? Status(info)
            : throw new OrchestratorException($"Unknown platform '{name}'.");

    public async Task<SetupResult> ConnectAsync(string name, CancellationToken ct = default)
    {
        var status = Get(name);
        var o = registry.OptionsFor(status.Name);
        var integration = PlatformIntegrations.For(status.Name, o)
            ?? throw new OrchestratorException($"{status.DisplayName} has no integration defined. Add McpAdd/SkillsDirs under its Integration settings.");
        var steps = new List<SetupStep>();
        var url = mcpUrl();
        var key = options().ApiKey is { Length: > 0 } k ? k : null;

        if (integration.AddByFile is not null)
        {
            steps.Add(Try("register MCP server in config file", () => integration.AddByFile(url, key) ?? "done"));
        }
        else if (integration.AddArgs is not null || integration.AddCommand is not null)
        {
            if (!status.Available && integration.AddArgs is not null)
            {
                steps.Add(new SetupStep("register MCP server", false, $"{status.DisplayName} CLI not found. {status.InstallHint}"));
            }
            else
            {
                // Remove first so re-running setup (e.g. after a port change) replaces the entry instead of failing.
                await RunAsync(integration.RemoveArgs is not null ? Prefix(status, integration.RemoveArgs) : Expand(integration.RemoveCommand, url), ct);
                var add = integration.AddArgs is not null ? Prefix(status, integration.AddArgs(url, key)) : Expand(integration.AddCommand, url);
                var (ok, output) = await RunAsync(add, ct);
                steps.Add(new SetupStep($"register MCP server ({string.Join(' ', add!.Take(4))} …)", ok, output));
                if (ok && integration.AddCommand is not null) SetCustomState(status.Name, true);
            }
        }

        foreach (var dir in integration.SkillsDirs)
        {
            steps.Add(Try($"install skill into {dir}", () => SkillInstaller.Install(dir, url.EndsWith("/mcp", StringComparison.Ordinal) ? url[..^4] : url)));
        }

        var after = Get(status.Name);
        return new SetupResult(steps.All(s => s.Ok), steps, after);
    }

    public async Task<SetupResult> DisconnectAsync(string name, CancellationToken ct = default)
    {
        var status = Get(name);
        var integration = PlatformIntegrations.For(status.Name, registry.OptionsFor(status.Name));
        var steps = new List<SetupStep>();
        if (integration is not null)
        {
            if (integration.RemoveByFile is not null)
            {
                steps.Add(Try("remove MCP server from config file", () => integration.RemoveByFile() ?? "not registered"));
            }
            else if (integration.RemoveArgs is not null || integration.RemoveCommand is not null)
            {
                var cmd = integration.RemoveArgs is not null ? Prefix(status, integration.RemoveArgs) : Expand(integration.RemoveCommand, mcpUrl());
                var (ok, output) = await RunAsync(cmd, ct);
                // "not found" style failures are fine when removing.
                steps.Add(new SetupStep("remove MCP server", true, ok ? output : $"(nothing to remove) {output}".Trim()));
                if (integration.RemoveCommand is not null) SetCustomState(status.Name, false);
            }
            foreach (var dir in integration.SkillsDirs)
            {
                steps.Add(Try($"remove skill from {dir}", () => SkillInstaller.Uninstall(dir) ? "removed" : "not installed by us"));
            }
        }
        return new SetupResult(true, steps, Get(status.Name));
    }

    /// <summary>Runs the agent's version command; no model call, no cost.</summary>
    public async Task<SetupResult> CheckAsync(string name, CancellationToken ct = default)
    {
        var status = Get(name);
        if (status.ResolvedPath is null)
        {
            return new SetupResult(false, [new SetupStep("find executable", false, $"'{status.Executable}' not found. {status.InstallHint}")], status);
        }
        var args = registry.OptionsFor(status.Name).VersionArgs is { Count: > 0 } v ? v : ["--version"];
        var (ok, output) = await RunAsync([status.ResolvedPath, .. args], ct);
        return new SetupResult(ok, [new SetupStep($"{Path.GetFileName(status.ResolvedPath)} {string.Join(' ', args)}", ok, output)], status);
    }

    private PlatformStatus Status(AgentInfo a)
    {
        var o = registry.OptionsFor(a.Name);
        var integration = PlatformIntegrations.For(a.Name, o);
        bool? registered = integration?.IsRegistered();
        if (registered is null && integration?.AddCommand is not null) registered = GetCustomState(a.Name);
        var skillDirs = integration?.SkillsDirs ?? [];
        return new PlatformStatus(
            a.Name, a.DisplayName, a.BuiltIn, a.Enabled, a.Available, a.Executable, a.ResolvedPath, a.InstallHint, a.Model,
            o.ExtraArgs ?? [], a.Protocol, integration is not null && (integration.CanRegister || skillDirs.Count > 0),
            registered,
            skillDirs.Count > 0 && skillDirs.All(SkillInstaller.IsInstalled),
            skillDirs.Select(SkillInstaller.SkillFile).ToList());
    }

    private static IReadOnlyList<string> Prefix(PlatformStatus status, IEnumerable<string> args) =>
        [status.ResolvedPath ?? status.Executable, .. args];

    private static IReadOnlyList<string>? Expand(IReadOnlyList<string>? command, string url) =>
        command?.Select(a => a.Replace("{url}", url).Replace("{name}", PlatformIntegration.ServerName)).ToList();

    private async Task<(bool Ok, string Output)> RunAsync(IReadOnlyList<string>? command, CancellationToken ct)
    {
        if (command is not { Count: > 0 }) return (true, "");
        var output = new StringBuilder();
        try
        {
            var outcome = await ProcessRunner.RunAsync(
                new ProcessSpec
                {
                    FileName = command[0],
                    Arguments = command.Skip(1).ToList(),
                    WorkingDirectory = OrchestratorPaths.Home,
                    Environment = EnvironmentScrubber.Scrubbed(options().ScrubEnvironmentVariables),
                    Timeout = TimeSpan.FromSeconds(90),
                },
                line => output.AppendLine(line),
                line => output.AppendLine(line),
                ct);
            return (outcome.Succeeded, Ansi.Strip(output.ToString()).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, $"could not start {command[0]}: {ex.Message}");
        }
    }

    private static SetupStep Try(string step, Func<object?> action)
    {
        try
        {
            return new SetupStep(step, true, action()?.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new SetupStep(step, false, ex.Message);
        }
    }

    private bool? GetCustomState(string name)
    {
        if (!File.Exists(_stateFile)) return false;
        var state = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_stateFile)) ?? [];
        return state.TryGetValue(name.ToLowerInvariant(), out var v) && v;
    }

    private void SetCustomState(string name, bool connected)
    {
        var state = File.Exists(_stateFile)
            ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_stateFile)) ?? []
            : [];
        state[name.ToLowerInvariant()] = connected;
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state));
    }
}
