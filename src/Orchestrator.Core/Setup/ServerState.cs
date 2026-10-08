using System.Text.Json;

namespace Orchestrator.Core.Setup;

/// <summary>What the running server writes to <see cref="OrchestratorPaths.ServerStateFile"/> for <c>orch</c> to find it.</summary>
public sealed record ServerState(int Pid, string Url, string DataDirectory, string Version, DateTimeOffset StartedAt)
{
    public static ServerState? Read(string? path = null)
    {
        path ??= OrchestratorPaths.ServerStateFile;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ServerState>(File.ReadAllText(path), OrchestratorJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Write(string? path = null)
    {
        path ??= OrchestratorPaths.ServerStateFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, OrchestratorJson.Options));
    }

    /// <summary>Removes the file only if it still describes this process (a newer server may have replaced it).</summary>
    public static void Delete(int pid, string? path = null)
    {
        path ??= OrchestratorPaths.ServerStateFile;
        if (Read(path)?.Pid == pid) File.Delete(path);
    }
}
