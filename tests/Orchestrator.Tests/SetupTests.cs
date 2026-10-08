using System.Text.Json.Nodes;
using Orchestrator.Core;
using Orchestrator.Core.Setup;

namespace Orchestrator.Tests;

/// <summary>Pure parts of the installer/setup library: config edits, command lines, skill files, autostart files.</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orch-setup-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void OpenCode_config_with_comments_keeps_other_settings_and_writes_a_backup()
    {
        var path = Path.Combine(_dir, "opencode.json");
        File.WriteAllText(path, """
            {
              // my settings
              "model": "anthropic/claude-sonnet",
              "mcp": { "other": { "type": "local", "command": ["x"] }, },
            }
            """);

        var note = OpenCodeConfig.AddServer(path, "http://127.0.0.1:7777/mcp");

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("anthropic/claude-sonnet", root["model"]!.GetValue<string>());
        Assert.NotNull(root["mcp"]!["other"]);
        Assert.Equal("remote", root["mcp"]!["orchestrator"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:7777/mcp", root["mcp"]!["orchestrator"]!["url"]!.GetValue<string>());
        Assert.Contains("// my settings", File.ReadAllText(path + ".bak"));
        Assert.Contains(".bak", note);
        Assert.True(OpenCodeConfig.HasServer(path));

        OpenCodeConfig.RemoveServer(path);
        Assert.False(OpenCodeConfig.HasServer(path));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path))!["mcp"]!["other"]);
    }

    [Fact]
    public void OpenCode_config_is_created_when_missing()
    {
        var path = Path.Combine(_dir, "sub", "opencode.json");
        Assert.Null(OpenCodeConfig.AddServer(path, "http://h/mcp", new Dictionary<string, string> { ["X-Orchestrator-Key"] = "k" }));
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("k", root["mcp"]!["orchestrator"]!["headers"]!["X-Orchestrator-Key"]!.GetValue<string>());
    }

    [Fact]
    public void Built_in_registration_commands_match_the_real_clis()
    {
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "http", "orchestrator", "http://u/mcp"],
            PlatformIntegrations.Claude().AddArgs!("http://u/mcp", null));
        Assert.Equal(["mcp", "remove", "orchestrator", "--scope", "user"], PlatformIntegrations.Claude().RemoveArgs);

        Assert.Equal(["mcp", "add", "orchestrator", "--url", "http://u/mcp"], PlatformIntegrations.Codex().AddArgs!("http://u/mcp", null));
        Assert.Equal(["mcp", "add", "orchestrator", "--url", "http://u/mcp", "--bearer-token-env-var", "ORCHESTRATOR_API_KEY"],
            PlatformIntegrations.Codex().AddArgs!("http://u/mcp", "secret"));

        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "http", "orchestrator", "http://u/mcp", "-H", "X-Orchestrator-Key: s"],
            PlatformIntegrations.Qoder().AddArgs!("http://u/mcp", "s"));

        var custom = PlatformIntegrations.For("gemini", new AgentOptions
        {
            Integration = new IntegrationOptions { McpAdd = ["gemini", "mcp", "add", "{name}", "{url}"], SkillsDirs = ["~/.gemini/skills"] },
        })!;
        Assert.Equal(["gemini", "mcp", "add", "{name}", "{url}"], custom.AddCommand);
        Assert.Equal(["~/.gemini/skills"], custom.SkillsDirs);
        Assert.Null(PlatformIntegrations.For("unknown", new AgentOptions()));
    }

    [Fact]
    public void Registration_is_detected_from_the_agents_own_config_files()
    {
        var toml = Path.Combine(_dir, "config.toml");
        File.WriteAllText(toml, "model = \"o3\"\n[mcp_servers.other]\nurl = \"x\"\n");
        Assert.False(PlatformIntegrations.TomlHasTable(toml, "mcp_servers.orchestrator"));
        File.AppendAllText(toml, "[mcp_servers.\"orchestrator\"]\nurl = \"y\"\n");
        Assert.True(PlatformIntegrations.TomlHasTable(toml, "mcp_servers.orchestrator"));

        var json = Path.Combine(_dir, "settings.json");
        File.WriteAllText(json, """{ "mcpServers": { "orchestrator": { "type": "http" } } }""");
        Assert.True(PlatformIntegrations.JsonHas(json, "mcpServers", "orchestrator"));
        Assert.False(PlatformIntegrations.JsonHas(Path.Combine(_dir, "missing.json"), "mcpServers", "orchestrator"));
        File.WriteAllText(json, "not json");
        Assert.Null(PlatformIntegrations.JsonHas(json, "mcpServers", "orchestrator"));
    }

    [Fact]
    public void Skill_install_replaces_our_legacy_copy_but_never_a_users_own_skill()
    {
        var skills = Path.Combine(_dir, "skills");
        var ours = Path.Combine(skills, "delegate", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(ours)!);
        File.WriteAllText(ours, "# Delegating work through the Agent Orchestrator\nCall delegate_task …");

        var file = SkillInstaller.Install(skills, "http://127.0.0.1:9999");

        Assert.Contains(SkillInstaller.Marker, File.ReadAllText(file));
        Assert.Contains("name: agent-orchestrator", File.ReadAllText(file));
        Assert.Contains("http://127.0.0.1:9999/", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ours)));

        // A user's own "delegate" skill is left alone.
        var theirs = Path.Combine(skills, "delegate", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        File.WriteAllText(theirs, "my own delegate skill");
        SkillInstaller.Install(skills);
        Assert.True(File.Exists(theirs));

        // Uninstall removes our copy only while it is still ours.
        Assert.True(SkillInstaller.Uninstall(skills));
        Assert.False(SkillInstaller.IsInstalled(skills));
        SkillInstaller.Install(skills);
        File.WriteAllText(SkillInstaller.SkillFile(skills), "edited by the user");
        Assert.False(SkillInstaller.Uninstall(skills));
        Assert.True(SkillInstaller.IsInstalled(skills));
    }

    [Fact]
    public void Autostart_definitions_quote_paths_and_carry_PATH()
    {
        var exe = "/home/a b/.local/share/agent-orchestrator/app/Orchestrator.Api";
        Assert.Equal(["/Create", "/F", "/SC", "ONLOGON", "/RL", "LIMITED", "/TN", "Agent Orchestrator", "/TR", "\"C:\\x y\\Orchestrator.Api.exe\""],
            Autostart.SchtasksCreateArgs("C:\\x y\\Orchestrator.Api.exe"));

        var unit = Autostart.SystemdUnit(exe, "/usr/bin:/home/a/.npm/bin");
        Assert.Contains($"ExecStart=\"{exe}\"", unit);
        Assert.Contains("Environment=\"PATH=/usr/bin:/home/a/.npm/bin\"", unit);
        Assert.Contains("WantedBy=default.target", unit);

        var plist = Autostart.LaunchAgentPlist("/Apps/A&B/Orchestrator.Api", "/usr/bin", "/tmp/log");
        Assert.Contains("<string>/Apps/A&amp;B/Orchestrator.Api</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(plist));
    }

    [Fact]
    public void User_config_edits_keep_unrelated_settings_and_match_names_case_insensitively()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            { "Urls": "http://127.0.0.1:8888", // comment
              "Orchestrator": { "MaxDepth": 3, "Agents": { "Claude": { "Model": "opus" } } } }
            """);
        var store = new UserConfigStore(path);

        store.UpdateAgent("claude", a => a["Enabled"] = false);
        store.UpdateAgent("gemini", a => a["Protocol"] = "text");

        var root = store.Load();
        Assert.Equal("http://127.0.0.1:8888", root["Urls"]!.GetValue<string>());
        Assert.Equal(3, root["Orchestrator"]!["MaxDepth"]!.GetValue<int>());
        Assert.Equal("opus", root["Orchestrator"]!["Agents"]!["Claude"]!["Model"]!.GetValue<string>());
        Assert.False(root["Orchestrator"]!["Agents"]!["Claude"]!["Enabled"]!.GetValue<bool>());
        Assert.True(store.RemoveAgent("GEMINI"));
        Assert.Null(store.Load()["Orchestrator"]!["Agents"]!["gemini"]);
    }
}
