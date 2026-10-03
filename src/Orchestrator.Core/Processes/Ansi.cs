using System.Text.RegularExpressions;

namespace Orchestrator.Core.Processes;

/// <summary>Removes terminal escape sequences (colours, cursor moves, OSC titles) from captured output.</summary>
public static partial class Ansi
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex EscapeSequence();

    public static string Strip(string text) =>
        text.Contains('\x1B') ? EscapeSequence().Replace(text, string.Empty) : text;

    public static string? StripNullable(string? text) => text is null ? null : Strip(text);
}
