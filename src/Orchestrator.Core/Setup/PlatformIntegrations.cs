using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orchestrator.Core.Setup;

/// <summary>
/// How one platform is connected to the orchestrator: registering the MCP server (by running the agent's own CLI or
/// editing its config file) and the skill folders to install into. Built-in platforms are defined here (verified against
/// the real CLIs); custom platforms come from <see cref="IntegrationOptions"/>.
/// </summary>
public sealed class PlatformIntegration
{
    public const string ServerName = "orchestrator";

    public required string Agent { get; init; }

    /// <summary>Arguments (after the agent executable) that register the server: (url, apiKey) → args.</summary>
    public Func<string, string?, IReadOnlyList<string>>? AddArgs { get; init; }

    public IReadOnlyList<string>? RemoveArgs { get; init; }

    /// <summary>For custom platforms: full commands, program first, with {url}/{name} placeholders.</summary>
    public IReadOnlyList<string>? AddCommand { get; init; }
    public IReadOnlyList<string>? RemoveCommand { get; init; }

    /// <summary>For platforms configured by file (OpenCode): (url, apiKey) → note.</summary>
    public Func<string, string?, string?>? AddByFile { get; init; }
    public Func<string?>? RemoveByFile { get; init; }

    /// <summary>True/false when the registration can be read from the agent's config; null when unknown.</summary>
    public Func<bool?> IsRegistered { get; init; } = () => null;

    public IReadOnlyList<string> SkillsDirs { get; init; } = [];

    public bool CanRegister => AddArgs is not null || AddCommand is not null || AddByFile is not null;
}

public static class PlatformIntegrations
{
    public static PlatformIntegration? For(string agent, AgentOptions options)
    {
        if (options.Integration is { } custom) return Custom(agent, custom);
        return agent.ToLowerInvariant() switch
        {
            "claude" => Claude(),
            "codex" => Codex(),
            "opencode" => OpenCode(),
            "qoder" => Qoder(),
            _ => null,
        };
    }

    /// <summary>Claude Code: <c>claude mcp add --scope user</c>; user-scope servers live in <c>~/.claude.json</c>.</summary>
    internal static PlatformIntegration Claude()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var claudeJson = configDir is { Length: > 0 } ? Path.Combine(configDir, ".claude.json") : Path.Combine(OrchestratorPaths.Home, ".claude.json");
        return new PlatformIntegration
        {
            Agent = "claude",
            AddArgs = (url, key) => key is null
                ? ["mcp", "add", "--scope", "user", "--transport", "http", PlatformIntegration.ServerName, url]
                : ["mcp", "add", "--scope", "user", "--transport", "http", PlatformIntegration.ServerName, url, "--header", $"X-Orchestrator-Key: {key}"],
            RemoveArgs = ["mcp", "remove", PlatformIntegration.ServerName, "--scope", "user"],
            IsRegistered = () => JsonHas(claudeJson, "mcpServers", PlatformIntegration.ServerName),
            SkillsDirs = [Path.Combine(configDir is { Length: > 0 } ? configDir : Path.Combine(OrchestratorPaths.Home, ".claude"), "skills")],
        };
    }

    /// <summary>Codex: <c>codex mcp add --url</c>; servers live in <c>$CODEX_HOME/config.toml</c>, skills in <c>$CODEX_HOME/skills</c>.</summary>
    internal static PlatformIntegration Codex()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(OrchestratorPaths.Home, ".codex");
        return new PlatformIntegration
        {
            Agent = "codex",
            AddArgs = (url, key) => key is null
                ? ["mcp", "add", PlatformIntegration.ServerName, "--url", url]
                : ["mcp", "add", PlatformIntegration.ServerName, "--url", url, "--bearer-token-env-var", "ORCHESTRATOR_API_KEY"],
            RemoveArgs = ["mcp", "remove", PlatformIntegration.ServerName],
            IsRegistered = () => TomlHasTable(Path.Combine(home, "config.toml"), $"mcp_servers.{PlatformIntegration.ServerName}"),
            SkillsDirs = [Path.Combine(home, "skills")],
        };
    }

    /// <summary>OpenCode has no non-interactive "mcp add": edit <c>~/.config/opencode/opencode.json</c>.</summary>
    internal static PlatformIntegration OpenCode(string? configPath = null)
    {
        var path = configPath ?? OpenCodeConfig.DefaultPath;
        return new PlatformIntegration
        {
            Agent = "opencode",
            AddByFile = (url, key) => OpenCodeConfig.AddServer(path, url,
                key is null ? null : new Dictionary<string, string> { ["X-Orchestrator-Key"] = key }),
            RemoveByFile = () => OpenCodeConfig.RemoveServer(path),
            IsRegistered = () => File.Exists(path) ? OpenCodeConfig.HasServer(path) : false,
            SkillsDirs = [Path.Combine(Path.GetDirectoryName(path)!, "skills")],
        };
    }

    /// <summary>Qoder: <c>qodercli mcp add --scope user</c>; user servers live in <c>~/.qoder/settings.json</c>.</summary>
    internal static PlatformIntegration Qoder()
    {
        var dir = Environment.GetEnvironmentVariable("QODER_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(OrchestratorPaths.Home, ".qoder");
        return new PlatformIntegration
        {
            Agent = "qoder",
            AddArgs = (url, key) => key is null
                ? ["mcp", "add", "--scope", "user", "--transport", "http", PlatformIntegration.ServerName, url]
                : ["mcp", "add", "--scope", "user", "--transport", "http", PlatformIntegration.ServerName, url, "-H", $"X-Orchestrator-Key: {key}"],
            RemoveArgs = ["mcp", "remove", PlatformIntegration.ServerName, "--scope", "user"],
            IsRegistered = () => JsonHas(Path.Combine(dir, "settings.json"), "mcpServers", PlatformIntegration.ServerName),
            SkillsDirs = [Path.Combine(dir, "skills")],
        };
    }

    internal static PlatformIntegration Custom(string agent, IntegrationOptions o) => new()
    {
        Agent = agent,
        AddCommand = o.McpAdd is { Count: > 0 } add ? add : null,
        RemoveCommand = o.McpRemove is { Count: > 0 } remove ? remove : null,
        SkillsDirs = o.SkillsDirs ?? [],
    };

    internal static bool? JsonHas(string path, string section, string key)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return root?[section]?[key] is JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool? TomlHasTable(string path, string table)
    {
        if (!File.Exists(path)) return false;
        var parts = table.Split('.');
        // [mcp_servers.orchestrator] or [mcp_servers."orchestrator"]
        var pattern = "^\\s*\\[\\s*" + string.Join("\\s*\\.\\s*", parts.Select(p => $"\"?{Regex.Escape(p)}\"?")) + "\\s*\\]";
        return File.ReadLines(path).Any(line => Regex.IsMatch(line, pattern));
    }
}
