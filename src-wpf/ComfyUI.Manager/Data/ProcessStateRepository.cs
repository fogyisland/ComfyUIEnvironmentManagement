using System;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// ProcessState:row of the <c>process_state</c> table.
/// </summary>
public sealed class ProcessState
{
    public string EnvId { get; set; } = "";
    public int Pid { get; set; }
    public int Port { get; set; }
    /// <summary>ISO 8601 naive timestamp, matches the Python writer.</summary>
    public string StartedAt { get; set; } = "";
}

/// <summary>
/// ProcessStateRepository:CRUD for the <c>process_state</c> table.
/// </summary>
public sealed class ProcessStateRepository
{
    private readonly SqliteConnectionFactory _factory;

    public ProcessStateRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public ProcessState? Get(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT env_id, pid, port, started_at
            FROM process_state WHERE env_id = @env_id";
        cmd.Parameters.AddWithValue("@env_id", envId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new ProcessState
        {
            EnvId = reader.GetString(0),
            Pid = reader.GetInt32(1),
            Port = reader.GetInt32(2),
            StartedAt = reader.GetString(3),
        };
    }

    public void Upsert(ProcessState state)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO process_state (env_id, pid, port, started_at)
            VALUES (@env_id, @pid, @port, @started_at)
            ON CONFLICT(env_id) DO UPDATE SET
                pid=excluded.pid,
                port=excluded.port,
                started_at=excluded.started_at";
        cmd.Parameters.AddWithValue("@env_id", state.EnvId);
        cmd.Parameters.AddWithValue("@pid", state.Pid);
        cmd.Parameters.AddWithValue("@port", state.Port);
        cmd.Parameters.AddWithValue("@started_at", state.StartedAt);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM process_state WHERE env_id = @env_id";
        cmd.Parameters.AddWithValue("@env_id", envId);
        cmd.ExecuteNonQuery();
    }
}
