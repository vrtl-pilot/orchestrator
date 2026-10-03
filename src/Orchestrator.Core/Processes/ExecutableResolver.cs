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

    /// <summary>Returns the full path of <paramref name="executable"/>, or null if it cannot be found.</summary>
    public static string? Find(string executable) =>
        Find(
            executable,
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExt);

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

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
