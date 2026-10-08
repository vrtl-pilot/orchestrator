using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orchestrator.Core.Setup;

/// <summary>
/// Adds/removes the orchestrator in OpenCode's global config (<c>~/.config/opencode/opencode.json</c>), which has no
/// non-interactive "mcp add" command. The file may be JSONC; comments are not preserved, so a backup is written first.
/// </summary>
public static class OpenCodeConfig
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(OrchestratorPaths.Home, ".config", "opencode", "opencode.json");

    public static bool HasServer(string path, string name = "orchestrator") =>
        Load(path)?["mcp"]?[name] is JsonObject;

    /// <summary>Returns a note for the user (e.g. that a backup was made), or null.</summary>
    public static string? AddServer(string path, string url, IReadOnlyDictionary<string, string>? headers = null, string name = "orchestrator")
    {
        var root = Load(path) ?? new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        if (root["mcp"] is not JsonObject mcp)
        {
            mcp = new JsonObject();
            root["mcp"] = mcp;
        }
        var entry = new JsonObject { ["type"] = "remote", ["url"] = url, ["enabled"] = true };
        if (headers is { Count: > 0 })
        {
            entry["headers"] = new JsonObject(headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)JsonValue.Create(h.Value))));
        }
        mcp[name] = entry;
        return Save(path, root);
    }

    public static string? RemoveServer(string path, string name = "orchestrator")
    {
        var root = Load(path);
        if (root?["mcp"] is not JsonObject mcp || !mcp.Remove(name)) return null;
        return Save(path, root);
    }

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object.");
    }

    private static string? Save(string path, JsonObject root)
    {
        string? note = null;
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
            note = $"backup saved as {path}.bak (comments in the original are not kept)";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(Indented));
        return note;
    }
}
