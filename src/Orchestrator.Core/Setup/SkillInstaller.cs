using System.Reflection;

namespace Orchestrator.Core.Setup;

/// <summary>
/// Installs the <c>agent-orchestrator</c> skill (embedded in this assembly from <c>skills/agent-orchestrator/SKILL.md</c>)
/// into an agent's user skill folder, and removes only copies it owns.
/// </summary>
public static class SkillInstaller
{
    public const string SkillName = "agent-orchestrator";

    /// <summary>Text present in every copy we write; used to recognise our own files before deleting anything.</summary>
    public const string Marker = "Installed by Agent Orchestrator";

    /// <summary>Folder name used by earlier versions, removed on install when it carries our content.</summary>
    private const string LegacyName = "delegate";

    public static string Content
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Orchestrator.Core.Skill.SKILL.md")
                ?? throw new InvalidOperationException("Embedded skill not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static string SkillFile(string skillsDir) => Path.Combine(OrchestratorPaths.ExpandHome(skillsDir), SkillName, "SKILL.md");

    public static bool IsInstalled(string skillsDir) => File.Exists(SkillFile(skillsDir));

    private const string DefaultBaseUrl = "http://127.0.0.1:7777";

    /// <summary>
    /// Writes (or updates) the skill and removes a legacy <c>delegate</c> copy if it is ours. <paramref name="baseUrl"/>
    /// replaces the default dashboard address when the server runs elsewhere.
    /// </summary>
    public static string Install(string skillsDir, string? baseUrl = null)
    {
        var file = SkillFile(skillsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var content = Content;
        if (baseUrl is { Length: > 0 } && baseUrl.TrimEnd('/') != DefaultBaseUrl) content = content.Replace(DefaultBaseUrl, baseUrl.TrimEnd('/'));
        File.WriteAllText(file, content);

        var legacy = Path.Combine(OrchestratorPaths.ExpandHome(skillsDir), LegacyName);
        if (IsOurs(Path.Combine(legacy, "SKILL.md"), legacyAllowed: true)) Directory.Delete(legacy, recursive: true);
        return file;
    }

    /// <summary>Removes our skill folder; leaves it alone if the user replaced the file with their own.</summary>
    public static bool Uninstall(string skillsDir)
    {
        var file = SkillFile(skillsDir);
        if (!IsOurs(file, legacyAllowed: false)) return false;
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        return true;
    }

    private static bool IsOurs(string file, bool legacyAllowed)
    {
        if (!File.Exists(file)) return false;
        var text = File.ReadAllText(file);
        return text.Contains(Marker, StringComparison.Ordinal)
               || (legacyAllowed && text.Contains("Agent Orchestrator", StringComparison.Ordinal) && text.Contains("delegate_task", StringComparison.Ordinal));
    }
}
