using System.Text.Json;

namespace Orchestrator.Core.Agents;

/// <summary>Tolerant helpers for reading agent JSONL output, whose shapes change between CLI versions.</summary>
internal static class JsonLine
{
    public static bool TryParse(string line, out JsonElement root)
    {
        root = default;
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? Str(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement? Obj(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? value
            : null;

    public static int? Int(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var i)
            ? i
            : null;

    public static bool Bool(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>Compact one-line rendering of a JSON value, truncated for display.</summary>
    public static string Compact(JsonElement? element, int max = 300)
    {
        if (element is null) return string.Empty;
        var text = element.Value.ValueKind == JsonValueKind.String
            ? element.Value.GetString() ?? string.Empty
            : element.Value.GetRawText();
        return Truncate(text.ReplaceLineEndings(" "), max);
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
