using Orchestrator.Core.Processes;

namespace Orchestrator.Tests;

public sealed class ExecutableResolverTests : IDisposable
{
    private const string WindowsPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WSF;.MSC";
    private readonly string _dir = Directory.CreateTempSubdirectory("orch-path-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name, bool executable = false)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    [Fact]
    public void Windows_prefers_the_cmd_shim_over_npms_extensionless_bash_shim()
    {
        // npm's global folder on Windows: opencode (bash script), opencode.cmd, opencode.ps1.
        Touch("opencode");
        Touch("opencode.ps1");
        var cmd = Touch("opencode.cmd");

        Assert.Equal(cmd, ExecutableResolver.Find("opencode", [_dir], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Windows_never_returns_an_extensionless_file()
    {
        Touch("codex");
        Assert.Null(ExecutableResolver.Find("codex", [_dir], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Windows_follows_PATHEXT_order()
    {
        Touch("codex.cmd");
        var exe = Touch("codex.exe");
        Assert.Equal(exe, ExecutableResolver.Find("codex", [_dir], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Windows_configured_full_path_without_extension_resolves_to_the_cmd_next_to_it()
    {
        Touch("opencode");
        var cmd = Touch("opencode.cmd");
        Assert.Equal(cmd, ExecutableResolver.Find(Path.Combine(_dir, "opencode"), [], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Windows_explicit_extension_is_used_as_is()
    {
        var cmd = Touch("claude.cmd");
        Assert.Equal(cmd, ExecutableResolver.Find("claude.cmd", [_dir], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Unix_requires_the_executable_bit()
    {
        if (OperatingSystem.IsWindows()) return;

        Touch("plainfile");
        var tool = Touch("agenttool", executable: true);

        Assert.Null(ExecutableResolver.Find("plainfile", [_dir], isWindows: false, ""));
        Assert.Equal(tool, ExecutableResolver.Find("agenttool", [_dir], isWindows: false, ""));
    }

    [Fact]
    public void Git_bash_is_found_next_to_git()
    {
        var gitExe = Path.Combine(_dir, "Git", "cmd", "git.exe");
        var bash = Path.Combine(_dir, "Git", "bin", "bash.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(gitExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(bash)!);
        File.WriteAllText(gitExe, "");
        File.WriteAllText(bash, "");

        Assert.Equal(bash, ExecutableResolver.FindGitBash(null, gitExe, programFiles: null));
    }

    [Fact]
    public void Git_bash_explicit_path_wins_but_WSL_bash_is_rejected()
    {
        var custom = Touch("my-bash.exe");
        Assert.Equal(custom, ExecutableResolver.FindGitBash(custom, null, null));

        var system32 = Path.Combine(_dir, "System32", "bash.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(system32)!);
        File.WriteAllText(system32, "");
        Assert.True(File.Exists(system32));
        Assert.Null(ExecutableResolver.FindGitBash(system32, null, null));
        Assert.Null(ExecutableResolver.FindGitBash(null, null, null));
    }

    [Fact]
    public void Windows_finds_an_exe_inside_a_folder_named_like_the_tool()
    {
        // Qoder's PowerShell installer: PATH contains ...\.qoder\bin\qodercli, which holds qodercli.exe.
        var toolDir = Path.Combine(_dir, ".qoder", "bin", "qodercli");
        Directory.CreateDirectory(toolDir);
        var exe = Path.Combine(toolDir, "qodercli.exe");
        File.WriteAllText(exe, "");

        Assert.Equal(exe, ExecutableResolver.Find("qodercli", [Path.Combine(_dir, ".qoder", "entry"), toolDir], isWindows: true, WindowsPathExt));
    }

    [Fact]
    public void Registry_paths_are_merged_after_the_process_path_without_duplicates()
    {
        var merged = ExecutableResolver.MergePaths(["C:/a", "C:/b/"], ["c:/B", "C:/machine"], ["\"C:/user\"", ""]);
        Assert.Equal(["C:/a", "C:/b", "C:/machine", "C:/user"], merged);
    }
}
