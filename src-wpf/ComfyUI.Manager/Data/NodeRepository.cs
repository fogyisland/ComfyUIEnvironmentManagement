using System;
using System.Collections.Generic;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// NodeRepository:CRUD for the <c>scanned_nodes</c> table.
/// </summary>
public sealed class NodeRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnectionFactory _factory;

    public NodeRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public List<ScannedNode> ListByEnv(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, package_path, version, author,
                   description, class_mappings, status, scan_meta,
                   last_scanned_at, locked
            FROM scanned_nodes WHERE env_id = @env ORDER BY package";
        cmd.Parameters.AddWithValue("@env", envId);
        using var reader = cmd.ExecuteReader();
        var list = new List<ScannedNode>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public ScannedNode? Get(string nodeId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, package_path, version, author,
                   description, class_mappings, status, scan_meta,
                   last_scanned_at, locked
            FROM scanned_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Upsert(ScannedNode node)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scanned_nodes
                (id, env_id, package, package_path, version, author,
                 description, class_mappings, status, scan_meta,
                 last_scanned_at, locked)
            VALUES
                (@id, @env_id, @package, @package_path, @version, @author,
                 @description, @class_mappings, @status, @scan_meta,
                 @last_scanned_at, @locked)
            ON CONFLICT(id) DO UPDATE SET
                package_path=excluded.package_path,
                version=excluded.version,
                author=excluded.author,
                description=excluded.description,
                class_mappings=excluded.class_mappings,
                status=excluded.status,
                scan_meta=excluded.scan_meta,
                last_scanned_at=excluded.last_scanned_at,
                locked=excluded.locked";
        Bind(cmd, node);
        cmd.ExecuteNonQuery();
    }

    public void SetLocked(string nodeId, bool locked)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE scanned_nodes SET locked = @locked WHERE id = @id";
        cmd.Parameters.AddWithValue("@locked", locked ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.ExecuteNonQuery();
    }

    public void SetStatus(string nodeId, string status)
    {
        if (status != "enabled" && status != "disabled")
        {
            throw new ArgumentException(
                $"status must be 'enabled' or 'disabled', got '{status}'",
                nameof(status));
        }

        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE scanned_nodes SET status = @status WHERE id = @id";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.ExecuteNonQuery();
    }

    private static ScannedNode Read(SqliteDataReader reader)
    {
        var classMappingsJson = reader.GetString(7);
        var scanMetaJson = reader.GetString(9);

        return new ScannedNode
        {
            Id = reader.GetString(0),
            EnvId = reader.GetString(1),
            Package = reader.GetString(2),
            PackagePath = reader.GetString(3),
            Version = reader.IsDBNull(4) ? null : reader.GetString(4),
            Author = reader.IsDBNull(5) ? null : reader.GetString(5),
            Description = reader.IsDBNull(6) ? null : reader.GetString(6),
            ClassMappings = JsonSerializer.Deserialize<List<string>>(
                classMappingsJson, JsonOptions) ?? new List<string>(),
            Status = reader.GetString(8),
            ScanMeta = JsonSerializer.Deserialize<Dictionary<string, string>>(
                scanMetaJson, JsonOptions) ?? new Dictionary<string, string>(),
            LastScannedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
            Locked = !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
        };
    }

    private static void Bind(SqliteCommand cmd, ScannedNode node)
    {
        cmd.Parameters.AddWithValue("@id", node.Id);
        cmd.Parameters.AddWithValue("@env_id", node.EnvId);
        cmd.Parameters.AddWithValue("@package", node.Package);
        cmd.Parameters.AddWithValue("@package_path", node.PackagePath);
        cmd.Parameters.AddWithValue("@version",
            (object?)node.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@author",
            (object?)node.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description",
            (object?)node.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@class_mappings",
            JsonSerializer.Serialize(node.ClassMappings));
        cmd.Parameters.AddWithValue("@status", node.Status);
        cmd.Parameters.AddWithValue("@scan_meta",
            JsonSerializer.Serialize(node.ScanMeta));
        cmd.Parameters.AddWithValue("@last_scanned_at",
            (object?)node.LastScannedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locked", node.Locked ? 1 : 0);
    }
}
