using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Storage;

public interface ITaskStore
{
    Task SaveAsync(AgentTask task, CancellationToken ct = default);
    Task<AgentTask?> GetAsync(string id, CancellationToken ct = default);
    Task<AgentTask?> FindByClientRequestIdAsync(string clientRequestId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentTask>> ListAsync(AgentTaskStatus? status = null, string? parentTaskId = null, int limit = 50, CancellationToken ct = default);
}

/// <summary>
/// Stores each task as a JSON document plus a few indexed columns. SQLite keeps the orchestrator
/// zero-ops and lets task state survive restarts of both the orchestrator and the calling agent.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private readonly string _connectionString;

    public SqliteTaskStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS tasks (
                id                TEXT PRIMARY KEY,
                status            TEXT NOT NULL,
                parent_id         TEXT NULL,
                client_request_id TEXT NULL,
                created_at        TEXT NOT NULL,
                json              TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status);
            CREATE INDEX IF NOT EXISTS ix_tasks_parent ON tasks(parent_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_tasks_client_request ON tasks(client_request_id) WHERE client_request_id IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    public async Task SaveAsync(AgentTask task, CancellationToken ct = default)
    {
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (id, status, parent_id, client_request_id, created_at, json)
            VALUES ($id, $status, $parent, $client, $created, $json)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, json = excluded.json;
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$parent", (object?)task.ParentTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$client", (object?)task.ClientRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(task, OrchestratorJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<AgentTask?> GetAsync(string id, CancellationToken ct = default) =>
        SingleAsync("SELECT json FROM tasks WHERE id = $v", id, ct);

    public Task<AgentTask?> FindByClientRequestIdAsync(string clientRequestId, CancellationToken ct = default) =>
        SingleAsync("SELECT json FROM tasks WHERE client_request_id = $v", clientRequestId, ct);

    public async Task<IReadOnlyList<AgentTask>> ListAsync(
        AgentTaskStatus? status = null, string? parentTaskId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        var where = new List<string>();
        if (status is not null)
        {
            where.Add("status = $status");
            command.Parameters.AddWithValue("$status", status.Value.ToString());
        }
        if (parentTaskId is not null)
        {
            where.Add("parent_id = $parent");
            command.Parameters.AddWithValue("$parent", parentTaskId);
        }
        command.CommandText = $"SELECT json FROM tasks {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var result = new List<AgentTask>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(Deserialize(reader.GetString(0)));
        }
        return result;
    }

    private async Task<AgentTask?> SingleAsync(string sql, string value, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$v", value);
        return await command.ExecuteScalarAsync(ct) is string json ? Deserialize(json) : null;
    }

    private static AgentTask Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentTask>(json, OrchestratorJson.Options)
        ?? throw new InvalidDataException("Corrupt task record.");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
