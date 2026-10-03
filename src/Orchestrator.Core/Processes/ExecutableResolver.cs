namespace Orchestrator.Core.Processes;

/// <summary>
/// Finds an executable on PATH. On Windows the agent CLIs are npm <c>.cmd</c> shims, which
/// <see cref="System.Diagnostics.Process"/> cannot start directly, so those are run through <c>cmd.exe /c</c>.
/// </summary>
public static class ExecutableResolver
{
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
    public static string? Find(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Prepend(string.Empty)
                .ToArray()
            : [string.Empty];

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), executable + ext);
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || ext.Length > 0 || IsExecutable(candidate)))
                {
                    return candidate;
                }
            }
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
