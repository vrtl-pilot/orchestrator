namespace Orchestrator.Core.Setup;

/// <summary>
/// Fixed per-user locations, identical for the server, the CLI and the installer:
/// <list type="bullet">
/// <item><c>Root</c>: <c>%LOCALAPPDATA%\agent-orchestrator</c> (Windows), <c>~/.local/share/agent-orchestrator</c> (Linux),
/// <c>~/Library/Application Support/agent-orchestrator</c> (macOS) — task data, logs, user config.</item>
/// <item><c>App</c>: <c>Root/app</c> — the installed program (server + <c>orch</c>, one self-contained folder), replaced on upgrade.</item>
/// </list>
/// </summary>
public static class OrchestratorPaths
{
    /// <summary>Per-user root folder. Override with <c>ORCHESTRATOR_HOME</c> (tests, portable installs).</summary>
    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ORCHESTRATOR_HOME") is { Length: > 0 } custom) return Path.GetFullPath(custom);
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            return string.IsNullOrEmpty(root) ? Path.GetFullPath("data") : Path.Combine(root, "agent-orchestrator");
        }
    }

    /// <summary>User settings, never touched by upgrades. Override with <c>ORCHESTRATOR_CONFIG</c>.</summary>
    public static string ConfigFile =>
        Environment.GetEnvironmentVariable("ORCHESTRATOR_CONFIG") is { Length: > 0 } custom ? custom : Path.Combine(Root, "config.json");

    public static string AppDirectory => Path.Combine(Root, "app");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Expands a leading <c>~</c> to the user's home folder.</summary>
    public static string ExpandHome(string path) =>
        path == "~" ? Home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Home, path[2..])
            : Environment.ExpandEnvironmentVariables(path);

    /// <summary>Written by the running server: pid, URL and data folder (read by <c>orch</c>).</summary>
    public static string ServerStateFile => Path.Combine(Root, "server.json");

    /// <summary>Executable name of the server inside <see cref="AppDirectory"/>.</summary>
    public static string ServerExecutableName => OperatingSystem.IsWindows() ? "Orchestrator.Api.exe" : "Orchestrator.Api";
}
