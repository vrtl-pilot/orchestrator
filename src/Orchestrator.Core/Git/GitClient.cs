using System.Text;
using Orchestrator.Core.Processes;

namespace Orchestrator.Core.Git;

public sealed class GitException(string message) : Exception(message);

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Thin wrapper over the <c>git</c> CLI (the same Git the agents themselves use).</summary>
public static class GitClient
{
    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        if (environment is not null)
        {
            foreach (var (k, v) in environment) env[k] = v;
        }

        var outcome = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                FileName = "git",
                Arguments = args.ToList(),
                WorkingDirectory = workingDirectory,
                Environment = env,
                Timeout = TimeSpan.FromMinutes(10),
            },
            line => stdout.AppendLine(line),
            line => stderr.AppendLine(line),
            cancellationToken);

        return new GitResult(outcome.ExitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
    }

    public static async Task<string> RunCheckedAsync(
        string workingDirectory,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var list = args.ToList();
        var result = await RunAsync(workingDirectory, list, environment, cancellationToken);
        if (!result.Ok)
        {
            throw new GitException($"git {string.Join(' ', list)} failed ({result.ExitCode}): {result.StdErr}");
        }
        return result.StdOut;
    }
}
