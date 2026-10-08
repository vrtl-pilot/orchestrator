namespace Orchestrator.Core.Agents;

/// <summary>
/// Turns allowed command prefixes into <c>--allowed-tools</c> rules for Claude Code and Qoder. Both refuse any shell
/// command that isn't pre-approved when they run headless without permission prompts. Each command gets rules for the
/// <c>Bash</c> tool and for the <c>PowerShell</c> tool (used on Windows), with and without arguments.
/// </summary>
public static class AllowedToolRules
{
    public static IEnumerable<string> Args(IEnumerable<string> commands)
    {
        foreach (var command in commands.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct())
        {
            // ')' would end the rule early; such commands can't be expressed as a rule.
            if (command.Contains(')')) continue;
            foreach (var tool in (string[])["Bash", "PowerShell"])
            {
                yield return "--allowed-tools";
                yield return $"{tool}({command})";
                yield return "--allowed-tools";
                yield return $"{tool}({command} *)";
            }
        }
    }
}
