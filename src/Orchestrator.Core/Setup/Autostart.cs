using System.Security;
using System.Text;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Setup;

public sealed record AutostartStatus(bool Supported, bool Enabled, string Mechanism, string? Location, string? Note = null);

/// <summary>
/// Starts the installed server when the user logs in: Task Scheduler (Windows), a systemd user unit (Linux) or a
/// LaunchAgent (macOS). The file/command builders are pure so they can be tested on any OS; the process calls are thin.
/// </summary>
public static class Autostart
{
    public const string WindowsTaskName = "Agent Orchestrator";
    public const string SystemdUnitName = "agent-orchestrator.service";
    public const string LaunchdLabel = "com.agent-orchestrator.server";

    public static string SystemdUnitPath => Path.Combine(OrchestratorPaths.Home, ".config", "systemd", "user", SystemdUnitName);
    public static string LaunchAgentPath => Path.Combine(OrchestratorPaths.Home, "Library", "LaunchAgents", LaunchdLabel + ".plist");

    /// <summary>
    /// The installed server executable, or null when running from a development build under the <c>dotnet</c> host
    /// (registering that would start a stale build at login).
    /// </summary>
    public static string? InstalledServer(string? serverDirectory = null)
    {
        var dir = serverDirectory ?? OrchestratorPaths.AppDirectory;
        var exe = Path.Combine(dir, OrchestratorPaths.ServerExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    // ---- pure builders ----

    public static IReadOnlyList<string> SchtasksCreateArgs(string serverExe) =>
        ["/Create", "/F", "/SC", "ONLOGON", "/RL", "LIMITED", "/TN", WindowsTaskName, "/TR", $"\"{serverExe}\""];

    public static IReadOnlyList<string> SchtasksDeleteArgs() => ["/Delete", "/F", "/TN", WindowsTaskName];
    public static IReadOnlyList<string> SchtasksQueryArgs() => ["/Query", "/TN", WindowsTaskName];

    /// <summary>
    /// The user's PATH is captured because systemd/launchd start services with a minimal PATH, where npm-installed
    /// agent CLIs would not be found.
    /// </summary>
    public static string SystemdUnit(string serverExe, string path) => $"""
        [Unit]
        Description=Agent Orchestrator (delegates coding tasks between agent CLIs)
        After=network.target

        [Service]
        Type=simple
        ExecStart="{serverExe}"
        WorkingDirectory={Path.GetDirectoryName(serverExe)}
        Environment="PATH={path}"
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    public static string LaunchAgentPlist(string serverExe, string path, string logFile) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{LaunchdLabel}</string>
          <key>ProgramArguments</key><array><string>{SecurityElement.Escape(serverExe)}</string></array>
          <key>WorkingDirectory</key><string>{SecurityElement.Escape(Path.GetDirectoryName(serverExe))}</string>
          <key>EnvironmentVariables</key><dict><key>PATH</key><string>{SecurityElement.Escape(path)}</string></dict>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
          <key>StandardOutPath</key><string>{SecurityElement.Escape(logFile)}</string>
          <key>StandardErrorPath</key><string>{SecurityElement.Escape(logFile)}</string>
        </dict>
        </plist>

        """;

    // ---- operations ----

    public static async Task<AutostartStatus> StatusAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var (ok, _) = await RunAsync("schtasks", SchtasksQueryArgs(), ct);
            return new AutostartStatus(true, ok, "Windows Task Scheduler", WindowsTaskName);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new AutostartStatus(true, File.Exists(LaunchAgentPath), "launchd LaunchAgent", LaunchAgentPath);
        }
        if (OperatingSystem.IsLinux())
        {
            var enabled = File.Exists(Path.Combine(OrchestratorPaths.Home, ".config", "systemd", "user", "default.target.wants", SystemdUnitName));
            return new AutostartStatus(true, enabled, "systemd user service", SystemdUnitPath);
        }
        return new AutostartStatus(false, false, "none", null, "Autostart is not supported on this OS; use 'orch start'.");
    }

    public static async Task<AutostartStatus> EnableAsync(string? serverExe = null, CancellationToken ct = default)
    {
        serverExe ??= InstalledServer();
        if (serverExe is null)
        {
            return new AutostartStatus(false, false, "none", null,
                "The orchestrator is not installed (running from a development build). Run install.ps1 / install.sh first.");
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        if (OperatingSystem.IsWindows())
        {
            var (ok, output) = await RunAsync("schtasks", SchtasksCreateArgs(serverExe), ct);
            return new AutostartStatus(true, ok, "Windows Task Scheduler", WindowsTaskName, ok ? null : output);
        }
        if (OperatingSystem.IsMacOS())
        {
            var log = Path.Combine(OrchestratorPaths.Root, "logs", "launchd.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
            File.WriteAllText(LaunchAgentPath, LaunchAgentPlist(serverExe, path, log));
            var domain = $"gui/{GetUid()}";
            await RunAsync("launchctl", ["bootout", domain, LaunchAgentPath], ct);
            var (ok, output) = await RunAsync("launchctl", ["bootstrap", domain, LaunchAgentPath], ct);
            return new AutostartStatus(true, true, "launchd LaunchAgent", LaunchAgentPath,
                ok ? null : $"Written, but launchctl said: {output}. It will load at next login.");
        }
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SystemdUnitPath)!);
            File.WriteAllText(SystemdUnitPath, SystemdUnit(serverExe, path));
            await RunAsync("systemctl", ["--user", "daemon-reload"], ct);
            var (ok, output) = await RunAsync("systemctl", ["--user", "enable", SystemdUnitName], ct);
            return new AutostartStatus(true, ok, "systemd user service", SystemdUnitPath,
                ok ? null : $"Unit written, but 'systemctl --user enable' failed: {output}");
        }
        return new AutostartStatus(false, false, "none", null, "Autostart is not supported on this OS; use 'orch start'.");
    }

    public static async Task<AutostartStatus> DisableAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunAsync("schtasks", SchtasksDeleteArgs(), ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(LaunchAgentPath))
            {
                await RunAsync("launchctl", ["bootout", $"gui/{GetUid()}", LaunchAgentPath], ct);
                File.Delete(LaunchAgentPath);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            await RunAsync("systemctl", ["--user", "disable", SystemdUnitName], ct);
            var link = Path.Combine(OrchestratorPaths.Home, ".config", "systemd", "user", "default.target.wants", SystemdUnitName);
            if (File.Exists(link)) File.Delete(link);
            if (File.Exists(SystemdUnitPath)) File.Delete(SystemdUnitPath);
            await RunAsync("systemctl", ["--user", "daemon-reload"], ct);
        }
        return await StatusAsync(ct);
    }

    private static string GetUid() => Environment.GetEnvironmentVariable("UID") is { Length: > 0 } uid ? uid : RunSync("id", "-u");

    private static string RunSync(string file, string arg)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arg) { RedirectStandardOutput = true });
            var text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "501";
        }
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var output = new StringBuilder();
        try
        {
            var outcome = await ProcessRunner.RunAsync(
                new ProcessSpec { FileName = file, Arguments = args, WorkingDirectory = OrchestratorPaths.Home, Timeout = TimeSpan.FromSeconds(30) },
                l => output.AppendLine(l), l => output.AppendLine(l), ct);
            return (outcome.Succeeded, output.ToString().Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OrchestratorException)
        {
            return (false, ex.Message);
        }
    }
}
