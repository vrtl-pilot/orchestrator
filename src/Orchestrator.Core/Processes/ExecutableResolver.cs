namespace Orchestrator.Core.Processes;

/// <summary>
/// Finds an executable on PATH. On Windows the agent CLIs are npm <c>.cmd</c> shims, which
/// <see cref="System.Diagnostics.Process"/> cannot start directly, so those are run through <c>cmd.exe /c</c>.
/// </summary>
public static class ExecutableResolver
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    public static (string FileName, IReadOnlyList<string> PrefixArgs) Resolve(string executable)
    {
        var path = Find(executable) ?? executable;
        if (OperatingSystem.IsWindows()
            && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return ("cmd.exe", ["/d", "/s", "/c", path]);
        }
        return (path, []);
    }

    /// <summary>
    /// Returns the full path of <paramref name="executable"/>, or null if it cannot be found.
    /// On Windows, if it is not on this process's PATH, the current user and machine PATH are read from the registry
    /// as well: a CLI installed after the orchestrator started (its PATH is a snapshot) is still found without a restart.
    /// </summary>
    public static string? Find(string executable)
    {
        var isWindows = OperatingSystem.IsWindows();
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExt;
        var processPath = SplitPath(Environment.GetEnvironmentVariable("PATH"));

        var found = Find(executable, processPath, isWindows, pathExt);
        if (found is not null || !isWindows) return found;

        var current = MergePaths(
            processPath,
            SplitPath(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)),
            SplitPath(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)));
        return Find(executable, current, isWindows, pathExt);
    }

    private static string[] SplitPath(string? path) =>
        (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Process PATH first, then directories only present in the registry copies (expanded, de-duplicated).</summary>
    internal static IReadOnlyList<string> MergePaths(params IEnumerable<string>[] sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var dir in sources.SelectMany(x => x))
        {
            var expanded = Environment.ExpandEnvironmentVariables(dir.Trim('"')).TrimEnd('\\', '/');
            if (expanded.Length > 0 && seen.Add(expanded)) result.Add(expanded);
        }
        return result;
    }

    /// <summary>
    /// Environment-independent lookup (testable on any OS).
    /// On Windows only files with a PATHEXT extension are runnable: npm installs an extensionless bash shim
    /// (<c>npm\opencode</c>) next to <c>opencode.cmd</c>, and the bare shim must never be chosen.
    /// </summary>
    internal static string? Find(string executable, IEnumerable<string> pathDirs, bool isWindows, string pathExt)
    {
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasRunnableExtension = extensions.Any(ext => executable.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        IEnumerable<string> Candidates(string basePath) =>
            !isWindows ? [basePath]
            : hasRunnableExtension ? [basePath]
            : extensions.Select(ext => basePath + ext.ToLowerInvariant());

        bool Usable(string candidate) => File.Exists(candidate) && (isWindows || IsExecutable(candidate));

        if (Path.IsPathRooted(executable) || executable.Contains('/') || executable.Contains('\\'))
        {
            return Candidates(executable).Where(Usable).Select(Path.GetFullPath).FirstOrDefault();
        }

        foreach (var dir in pathDirs)
        {
            var match = Candidates(Path.Combine(dir.Trim('"'), executable)).FirstOrDefault(Usable);
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>
    /// Locates Git for Windows' <c>bash.exe</c> (which ships grep, sed, etc.) so POSIX-style test commands work on Windows.
    /// Order: <c>CLAUDE_CODE_GIT_BASH_PATH</c>, next to the <c>git.exe</c> on PATH, <c>%ProgramFiles%\Git</c>.
    /// Never returns <c>System32\bash.exe</c>, which is WSL (a different filesystem).
    /// </summary>
    public static string? FindGitBash() =>
        FindGitBash(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH"),
            Find("git"),
            Environment.GetEnvironmentVariable("ProgramFiles"));

    internal static string? FindGitBash(string? explicitPath, string? gitExePath, string? programFiles)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        if (!string.IsNullOrWhiteSpace(gitExePath))
        {
            // <git>\cmd\git.exe, <git>\bin\git.exe or <git>\mingw64\bin\git.exe -> <git>\bin\bash.exe
            var dir = Path.GetDirectoryName(gitExePath);
            for (var i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            {
                candidates.Add(Path.Combine(dir, "bin", "bash.exe"));
            }
        }
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));

        return candidates.FirstOrDefault(c =>
            File.Exists(c) && !c.Replace('/', '\\').Contains("\\System32\\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
