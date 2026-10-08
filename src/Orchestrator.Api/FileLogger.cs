using System.Collections.Concurrent;
using System.Text;

namespace Orchestrator.Api;

/// <summary>
/// Minimal append-only file logger. When the server is started at login (no console window), this file is the only
/// place its messages go. Rolls over to <c>.1</c> at 5 MB.
/// </summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public string Path { get; } = path;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the server down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information
            && !(logLevel < LogLevel.Warning && category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => logLevel.ToString().ToLowerInvariant(),
            };
            var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null) text += exception + Environment.NewLine;
            provider.Write(text);
        }
    }
}
