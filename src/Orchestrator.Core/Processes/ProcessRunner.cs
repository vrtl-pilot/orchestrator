using System.Diagnostics;
using System.Text;

namespace Orchestrator.Core.Processes;

public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }

    /// <summary>Written to stdin, which is then closed. Prompts go here rather than argv (no quoting or length limits).</summary>
    public string? StandardInput { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan? Timeout { get; init; }

    /// <summary>Kill the process if it writes nothing to stdout/stderr for this long.</summary>
    public TimeSpan? IdleTimeout { get; init; }
}

public enum ProcessEndReason
{
    Exited,
    TimedOut,
    IdleTimedOut,
    Cancelled,
}

public sealed record ProcessOutcome(int ExitCode, ProcessEndReason Reason)
{
    public bool Succeeded => Reason == ProcessEndReason.Exited && ExitCode == 0;
}

/// <summary>Runs a child process, streams its output line by line and always cleans up the whole process tree.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        ProcessSpec spec,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken cancellationToken)
    {
        var (fileName, prefixArgs) = ExecutableResolver.Resolve(spec.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var arg in prefixArgs.Concat(spec.Arguments))
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in spec.Environment)
        {
            if (value is null) startInfo.Environment.Remove(key);
            else startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{spec.FileName}'.");
        }

        long lastActivityTicks = Environment.TickCount64;
        void Touch() => Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);

        var stdoutTask = PumpAsync(process.StandardOutput, line => { Touch(); onStdout(line); });
        var stderrTask = PumpAsync(process.StandardError, line => { Touch(); onStderr(line); });

        try
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited before reading stdin; its exit code tells the story.
        }

        var startedTicks = Environment.TickCount64;
        var reason = ProcessEndReason.Exited;
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        while (!exitTask.IsCompleted)
        {
            var tick = Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            await Task.WhenAny(exitTask, tick);
            if (exitTask.IsCompleted) break;

            var now = Environment.TickCount64;
            if (cancellationToken.IsCancellationRequested)
            {
                reason = ProcessEndReason.Cancelled;
            }
            else if (spec.Timeout is { } timeout && now - startedTicks > timeout.TotalMilliseconds)
            {
                reason = ProcessEndReason.TimedOut;
            }
            else if (spec.IdleTimeout is { } idle && idle > TimeSpan.Zero
                     && now - Interlocked.Read(ref lastActivityTicks) > idle.TotalMilliseconds)
            {
                reason = ProcessEndReason.IdleTimedOut;
            }

            if (reason != ProcessEndReason.Exited)
            {
                KillTree(process);
                break;
            }
        }

        await exitTask;
        // Drain remaining output (bounded: a grandchild holding the pipe open must not hang us).
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

        return new ProcessOutcome(reason == ProcessEndReason.Exited ? process.ExitCode : -1, reason);
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                onLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
