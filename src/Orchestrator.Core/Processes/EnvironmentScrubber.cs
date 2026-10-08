namespace Orchestrator.Core.Processes;

/// <summary>
/// Builds the environment overrides that remove inherited session/IPC variables (null = remove) before starting an
/// agent CLI. Needed whenever the orchestrator itself runs inside an agent session.
/// </summary>
public static class EnvironmentScrubber
{
    public static Dictionary<string, string?> Scrubbed(IEnumerable<string> patterns)
    {
        var list = patterns.ToList();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (list.Any(pattern => pattern.EndsWith('*')
                    ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                    : name.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                env[name] = null;
            }
        }
        return env;
    }
}
