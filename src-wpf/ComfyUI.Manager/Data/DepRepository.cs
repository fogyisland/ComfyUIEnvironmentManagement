using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// DepRepository:CRUD for the <c>dep_records</c> table.
/// </summary>
public sealed class DepRepository
{
    private readonly SqliteConnectionFactory _factory;

    public DepRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public List<DepRecord> ListByEnvAndPackage(string envId, string package)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, source, dep_name,
                   dep_version_spec, scanned_at
            FROM dep_records
            WHERE env_id = @env_id AND package = @package
            ORDER BY source, dep_name";
        cmd.Parameters.AddWithValue("@env_id", envId);
        cmd.Parameters.AddWithValue("@package", package);
        using var reader = cmd.ExecuteReader();
        var list = new List<DepRecord>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public List<DepRecord> ListByEnvAndDep(string envId, string depName)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, source, dep_name,
                   dep_version_spec, scanned_at
            FROM dep_records
            WHERE env_id = @env_id AND dep_name = @dep_name";
        cmd.Parameters.AddWithValue("@env_id", envId);
        cmd.Parameters.AddWithValue("@dep_name", depName);
        using var reader = cmd.ExecuteReader();
        var list = new List<DepRecord>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public void Upsert(DepRecord record)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO dep_records
                (id, env_id, package, source, dep_name,
                 dep_version_spec, scanned_at)
            VALUES
                (@id, @env_id, @package, @source, @dep_name,
                 @dep_version_spec, @scanned_at)
            ON CONFLICT(env_id, package, source, dep_name) DO UPDATE SET
                dep_version_spec=excluded.dep_version_spec,
                scanned_at=excluded.scanned_at";
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@env_id", record.EnvId);
        cmd.Parameters.AddWithValue("@package", record.Package);
        cmd.Parameters.AddWithValue("@source", record.Source);
        cmd.Parameters.AddWithValue("@dep_name", record.DepName);
        cmd.Parameters.AddWithValue("@dep_version_spec",
            (object?)record.DepVersionSpec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scanned_at", record.ScannedAt);
        cmd.ExecuteNonQuery();
    }

    private static DepRecord Read(SqliteDataReader reader)
    {
        return new DepRecord
        {
            Id = reader.GetString(0),
            EnvId = reader.GetString(1),
            Package = reader.GetString(2),
            Source = reader.GetString(3),
            DepName = reader.GetString(4),
            DepVersionSpec = reader.IsDBNull(5) ? null : reader.GetString(5),
            ScannedAt = reader.GetString(6),
        };
    }
}
