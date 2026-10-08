using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orchestrator.Core.Setup;

// `orch setup | start | stop | status | open | uninstall`: run the installed server in the background and drive its
// /api/setup endpoints (the same ones the dashboard's Setup page uses).
internal static partial class Cli
{
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>--url, ORCHESTRATOR_URL, the running server's address, the Urls in config.json, or the default.</summary>
    private static string ServerUrl(Dictionary<string, string?> flags)
    {
        var url = flags.GetValueOrDefault("url")
                  ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_URL")
                  ?? ReadJson(OrchestratorPaths.ServerStateFile)?["url"]?.GetValue<string>()
                  ?? ReadJson(OrchestratorPaths.ConfigFile)?["Urls"]?.GetValue<string>()?.Split(';')[0]
                  ?? "http://127.0.0.1:7777";
        return url.Replace("0.0.0.0", "127.0.0.1").Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").TrimEnd('/');
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: Jsonc) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<JsonNode?> HealthAsync(string baseUrl)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var response = await probe.GetAsync(baseUrl + "/health");
            return response.IsSuccessStatusCode ? await ReadAsync(response) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>The installed server: next to this orch (both live in the app folder), or in the per-user app folder.</summary>
    private static string? FindServer()
    {
        if (Environment.GetEnvironmentVariable("ORCHESTRATOR_SERVER") is { Length: > 0 } custom && File.Exists(custom)) return custom;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, OrchestratorPaths.ServerExecutableName),
            Path.Combine(OrchestratorPaths.AppDirectory, OrchestratorPaths.ServerExecutableName),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<int> StartAsync(HttpClient http, string baseUrl, bool quiet)
    {
        if (await HealthAsync(baseUrl) is { } running)
        {
            if (!quiet) PrintAddresses($"Already running (v{running["version"]}, pid {running["pid"]}).", baseUrl);
            return 0;
        }

        var server = FindServer();
        if (server is null)
        {
            Console.Error.WriteLine("The orchestrator server is not installed. Run install.ps1 (Windows) or ./install.sh from the repository,");
            Console.Error.WriteLine("or start a development build with: dotnet run --project src/Orchestrator.Api");
            return 1;
        }

        var dir = Path.GetDirectoryName(server)!;
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            // Published as a windowless app, so no console window appears.
            start = new ProcessStartInfo(server) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = dir };
        }
        else
        {
            // Detach from this terminal so closing it does not stop the server; output goes to the server's log file.
            start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = dir };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("nohup \"$0\" >/dev/null 2>&1 </dev/null &");
            start.ArgumentList.Add(server);
        }
        using (Process.Start(start))
        {
        }

        if (!quiet) Console.Write("Starting the orchestrator");
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            // The server may listen on a configured port other than the one we guessed.
            var url = ServerUrl([]);
            if (await HealthAsync(url) is { } health)
            {
                if (!quiet)
                {
                    Console.WriteLine();
                    PrintAddresses($"Started (v{health["version"]}, pid {health["pid"]}).", url);
                }
                if (http.BaseAddress?.ToString() != url + "/") http.BaseAddress = new Uri(url + "/"); // before any request on it
                return 0;
            }
            if (!quiet) Console.Write('.');
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine($"The server did not answer at {baseUrl} within 30 seconds. See its log: {Path.Combine(OrchestratorPaths.Root, "logs", "server.log")}");
        return 1;
    }

    private static async Task<int> StopAsync(HttpClient http, string baseUrl)
    {
        if (await HealthAsync(baseUrl) is null)
        {
            Console.WriteLine("Not running.");
            return 0;
        }
        var response = await http.PostAsync("api/admin/shutdown", null);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(Error(await ReadAsync(response)));
            return 1;
        }
        for (var i = 0; i < 40 && await HealthAsync(baseUrl) is not null; i++) await Task.Delay(250);
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task<int> StatusAsync(HttpClient http, string baseUrl)
    {
        if (await HealthAsync(baseUrl) is not { } health)
        {
            Console.WriteLine($"Not running (looked at {baseUrl}). Start it with `orch start`.");
            return 3;
        }
        var info = await GetJsonAsync(http, "api/setup/info");
        var platforms = await GetJsonAsync(http, "api/setup/platforms") as JsonArray ?? [];
        PrintAddresses($"Agent Orchestrator v{health["version"]} is running (pid {health["pid"]}).", baseUrl);
        Console.WriteLine($"  Data       {info?["dataDirectory"]}");
        Console.WriteLine($"  Settings   {info?["configFile"]}");
        Console.WriteLine($"  Log        {info?["logFile"] ?? "off"}");
        var autostart = info?["autostart"];
        Console.WriteLine($"  Autostart  {(autostart?["enabled"]?.GetValue<bool>() == true ? "on" : "off")} ({autostart?["mechanism"]})");
        Console.WriteLine();
        PrintPlatforms(platforms);
        return 0;
    }

    private static async Task<int> OpenAsync(HttpClient http, string baseUrl, List<string> positional)
    {
        if (await StartAsync(http, baseUrl, quiet: true) != 0) return 1;
        var root = http.BaseAddress!.ToString().TrimEnd('/');
        var target = positional.FirstOrDefault() switch
        {
            null or "" or "dashboard" => root + "/",
            "setup" => root + "/#setup",
            var id => $"{root}/#task={Uri.EscapeDataString(id)}",
        };
        Console.WriteLine(target);
        OpenBrowser(target);
        return 0;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url)?.Dispose();
            else Process.Start(new ProcessStartInfo("xdg-open", url) { RedirectStandardError = true, RedirectStandardOutput = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser available (e.g. SSH session); the URL was printed.
        }
    }

    private static async Task<int> SetupAsync(HttpClient http, string baseUrl, Dictionary<string, string?> flags)
    {
        Console.WriteLine("Agent Orchestrator setup");
        Console.WriteLine();
        if (await StartAsync(http, baseUrl, quiet: true) != 0) return 1;
        var url = http.BaseAddress!.ToString().TrimEnd('/');

        var platforms = (await GetJsonAsync(http, "api/setup/platforms") as JsonArray ?? []).OfType<JsonObject>().ToList();
        var info = await GetJsonAsync(http, "api/setup/info");
        var assumeYes = flags.ContainsKey("yes") || Console.IsInputRedirected;

        Console.WriteLine("Agent CLIs on this computer:");
        foreach (var p in platforms)
        {
            var available = p["available"]?.GetValue<bool>() == true;
            var where = available ? p["resolvedPath"]?.ToString() : $"not installed — {p["installHint"]}";
            if (p["enabled"]?.GetValue<bool>() == false) where = "disabled in settings";
            Mark(available, $"{p["displayName"],-14} {where}");
        }
        Console.WriteLine();

        HashSet<string>? requested = null;
        if (flags.GetValueOrDefault("agents") is { } list)
        {
            requested = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var unknown in requested.Where(r => platforms.All(p => !r.Equals(p["name"]?.ToString(), StringComparison.OrdinalIgnoreCase))))
            {
                Console.Error.WriteLine($"Unknown platform '{unknown}'. Known: {string.Join(", ", platforms.Select(p => p["name"]))}");
                return 1;
            }
        }

        var candidates = platforms.Where(p => p["available"]?.GetValue<bool>() == true && p["canConnect"]?.GetValue<bool>() == true).ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("No supported agent CLI was found. Install one (see the hints above), then run `orch setup` again.");
        }
        else if (requested is null)
        {
            Console.WriteLine("Connecting an agent lets you delegate from inside it (\"ask codex to …\"). It registers this");
            Console.WriteLine($"orchestrator's MCP server ({url}/mcp) with the agent and installs the agent-orchestrator skill.");
            Console.WriteLine();
        }

        var failures = 0;
        foreach (var p in candidates)
        {
            var name = p["name"]!.ToString();
            var connected = p["connected"]?.GetValue<bool>() == true;
            var connect = requested is not null
                ? requested.Contains(name)
                : assumeYes || Ask($"Connect {p["displayName"]}{(connected ? " (already connected; refresh)" : "")}?", true);
            if (!connect) continue;

            var response = await http.PostAsync($"api/setup/platforms/{Uri.EscapeDataString(name)}/connect", null);
            var result = await ReadAsync(response);
            var ok = response.IsSuccessStatusCode && result?["ok"]?.GetValue<bool>() == true;
            Mark(ok, $"{p["displayName"]}: {(ok ? "connected" : "connection had problems")}");
            foreach (var step in (result?["steps"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (step["ok"]?.GetValue<bool>() == true) continue;
                Console.WriteLine($"      {step["step"]}: {step["detail"]}");
            }
            if (!response.IsSuccessStatusCode) Console.WriteLine($"      {Error(result)}");
            if (!ok) failures++;
        }
        if (requested is not null)
        {
            foreach (var name in requested.Where(r => candidates.All(c => !r.Equals(c["name"]?.ToString(), StringComparison.OrdinalIgnoreCase))))
            {
                Mark(false, $"{name}: not installed or cannot be connected automatically — skipped");
                failures++;
            }
        }

        // Autostart only makes sense for the installed server (not a development build).
        Console.WriteLine();
        var installed = info?["installed"]?.GetValue<bool>() == true;
        var autostartOn = info?["autostart"]?["enabled"]?.GetValue<bool>() == true;
        if (!installed)
        {
            Console.WriteLine("Autostart: skipped (running from a development build; install with install.ps1 / install.sh).");
        }
        else if (!flags.ContainsKey("no-autostart")
                 && (flags.ContainsKey("autostart") || assumeYes || Ask("Start the orchestrator automatically when you log in?", true)))
        {
            var r = await ReadAsync(await http.PostAsJsonAsync("api/setup/autostart", new { enabled = true }));
            Mark(r?["enabled"]?.GetValue<bool>() == true, $"Autostart: {r?["mechanism"]} {r?["note"]}".TrimEnd());
        }
        else if (autostartOn && flags.ContainsKey("no-autostart"))
        {
            var r = await ReadAsync(await http.PostAsJsonAsync("api/setup/autostart", new { enabled = false }));
            Mark(true, $"Autostart turned off ({r?["mechanism"]}).");
        }

        Console.WriteLine();
        PrintAddresses("Ready.", url);
        Console.WriteLine();
        Console.WriteLine("Next: open a connected agent in any Git project and say e.g. \"delegate writing the tests to another agent\".");
        Console.WriteLine("It will show the available platforms and ask which one to use. Restart agents that were already open.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> UninstallAsync(HttpClient http, string baseUrl, Dictionary<string, string?> flags)
    {
        if (await StartAsync(http, baseUrl, quiet: true) != 0)
        {
            Console.Error.WriteLine("Could not start the server to disconnect agents; nothing was changed.");
            return 1;
        }
        var platforms = (await GetJsonAsync(http, "api/setup/platforms") as JsonArray ?? []).OfType<JsonObject>();
        foreach (var p in platforms.Where(p => p["mcpRegistered"]?.GetValue<bool>() == true || p["skillInstalled"]?.GetValue<bool>() == true))
        {
            var response = await http.PostAsync($"api/setup/platforms/{Uri.EscapeDataString(p["name"]!.ToString())}/disconnect", null);
            Mark(response.IsSuccessStatusCode, $"{p["displayName"]}: disconnected");
        }
        var r = await ReadAsync(await http.PostAsJsonAsync("api/setup/autostart", new { enabled = false }));
        Mark(r?["enabled"]?.GetValue<bool>() != true, "Autostart removed");
        await StopAsync(http, http.BaseAddress!.ToString().TrimEnd('/'));
        Console.WriteLine($"Task data and settings are kept in {OrchestratorPaths.Root} (delete it to remove them).");
        return 0;
    }

    private static void PrintAddresses(string headline, string url)
    {
        Console.WriteLine(headline);
        Console.WriteLine($"  Dashboard  {url}/");
        Console.WriteLine($"  Setup      {url}/#setup");
        Console.WriteLine($"  One task   {url}/#task=<task-id>");
        Console.WriteLine($"  MCP        {url}/mcp   (for agents, not a web page)");
    }

    private static void PrintPlatforms(JsonArray platforms)
    {
        Console.WriteLine("Platforms:");
        foreach (var p in platforms.OfType<JsonObject>())
        {
            var available = p["available"]?.GetValue<bool>() == true;
            var state = !available ? "not installed"
                : p["connected"]?.GetValue<bool>() == true ? "connected"
                : "installed, not connected";
            var model = p["model"]?.ToString() is { Length: > 0 } m ? $"model {m}" : "agent's default model";
            Mark(available, $"{p["displayName"],-14} {state,-25} {(available ? model : p["installHint"])}");
        }
    }

    private static void Mark(bool ok, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.DarkGray;
        Console.Write(ok ? "  ✔ " : "  ✘ ");
        Console.ForegroundColor = previous;
        Console.WriteLine(text);
    }

    private static bool Ask(string question, bool defaultYes)
    {
        Console.Write($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")} ");
        var answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(answer)) return defaultYes;
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, string path)
    {
        var response = await http.GetAsync(path);
        var body = await ReadAsync(response);
        return response.IsSuccessStatusCode ? body : throw new HttpRequestException(Error(body));
    }

    static Cli()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
    }
}
