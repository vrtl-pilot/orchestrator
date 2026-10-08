using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orchestrator.Core.Setup;

/// <summary>
/// Reads and writes the user's <c>config.json</c> (same shape as appsettings.json, under <c>"Orchestrator"</c>).
/// The server loads it with reload-on-change, so edits from the Setup page apply without a restart.
/// </summary>
public sealed class UserConfigStore(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Lock WriteLock = new();

    public string Path { get; } = path;

    public JsonObject Load()
    {
        if (!File.Exists(Path)) return [];
        var text = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? [];
    }

    /// <summary>Applies <paramref name="edit"/> to the agent's object (created if missing) and saves atomically.</summary>
    public void UpdateAgent(string name, Action<JsonObject> edit)
    {
        lock (WriteLock)
        {
            var root = Load();
            var agents = Child(Child(root, "Orchestrator"), "Agents");
            var key = agents.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            var agent = Child(agents, key);
            edit(agent);
            Save(root);
        }
    }

    public bool RemoveAgent(string name)
    {
        lock (WriteLock)
        {
            var root = Load();
            if (root["Orchestrator"]?["Agents"] is not JsonObject agents) return false;
            var key = agents.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            agents.Remove(key);
            Save(root);
            return true;
        }
    }

    /// <summary>Ensures the file exists (so the server can watch it) without changing existing content.</summary>
    public void EnsureExists()
    {
        lock (WriteLock)
        {
            if (!File.Exists(Path)) Save(new JsonObject { ["Orchestrator"] = new JsonObject { ["Agents"] = new JsonObject() } });
        }
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented));
        File.Move(temp, Path, overwrite: true);
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
