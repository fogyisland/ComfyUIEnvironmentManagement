using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// NodeRepository:CRUD for the <c>scanned_nodes</c> table.
/// </summary>
public sealed class NodeRepository : INodeRepository
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

    public async Task<long> CountAllAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scanned_nodes";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public List<ScannedNode> ListByEnv(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, package_path, version, author,
                   description, class_mappings, status, scan_meta,
                   last_scanned_at, locked, source
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

    /// <summary>
    /// v0.6.15:列所有 Source="download" 的行(本地下载的 sentinel 行)。
    /// 用于 LocalNodeService.ListAsync 扫 DB 端。
    /// </summary>
    public List<ScannedNode> ListDownloadedNodes()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, env_id, package, package_path, version, author,
                   description, class_mappings, status, scan_meta,
                   last_scanned_at, locked, source
            FROM scanned_nodes
            WHERE env_id = '' AND source = 'download'
            ORDER BY package";
        using var reader = cmd.ExecuteReader();
        var list = new List<ScannedNode>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>
    /// v0.6.15:查本地节点 (nodeId) 装到了哪些 env —— 走 package = ? 不用 id = ?
    /// (本地下载行 id 跟 env 装行 id 都 = nodeId,但 env 行 package 字段也存 nodeId)。
    /// 返回 env_id 列表(已装 env 的 Id 集合)。
    /// </summary>
    public IReadOnlyList<string> GetInstalledEnvIdsByPackage(string nodeId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT env_id FROM scanned_nodes
            WHERE package = @pkg AND env_id != '' AND source = 'env'";
        cmd.Parameters.AddWithValue("@pkg", nodeId);
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
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
                   last_scanned_at, locked, source
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
                 last_scanned_at, locked, source)
            VALUES
                (@id, @env_id, @package, @package_path, @version, @author,
                 @description, @class_mappings, @status, @scan_meta,
                 @last_scanned_at, @locked, @source)
            ON CONFLICT(id) DO UPDATE SET
                package_path=excluded.package_path,
                version=excluded.version,
                author=excluded.author,
                description=excluded.description,
                class_mappings=excluded.class_mappings,
                status=excluded.status,
                scan_meta=excluded.scan_meta,
                last_scanned_at=excluded.last_scanned_at,
                locked=excluded.locked,
                source=excluded.source";
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

    /// <summary>
    /// 删一行 ScannedNode。不级联到其它表
    /// (<c>node_versions</c> 是 catalog 的 LatestVersion,不是 per-env install)。
    /// 调用方(如 <c>NodeOperations.UninstallAsync</c>)负责先抓行拿到 PackagePath,
    /// 再删目录 + 调本方法。
    /// 不存在不抛异常。
    /// </summary>
    public void Delete(string nodeId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scanned_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// v0.6.15:按 (id, env_id, source) 三元组删除一行,只动匹配的行。
    /// 用于 LocalNodeService.DeleteAsync(只删 EnvId="" + Source="download" 的本地下载行,
    /// 不影响已装到 env 的 Source="env" 行)。不存在不抛。
    /// </summary>
    public void DeleteBySourceAndEnvId(string id, string envId, string source)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scanned_nodes WHERE id = @id AND env_id = @env_id AND source = @source";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@env_id", envId);
        cmd.Parameters.AddWithValue("@source", source);
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
            // 12 = source。NOT NULL DEFAULT 'env',直接 GetString;极老行 NULL 容错(虽然 EnsureColumn 会 backfill)
            Source = reader.IsDBNull(12) ? "env" : reader.GetString(12),
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
        // source 用模型默认(默认 "env");空串/null 视作 "env",跟 Read 端对称
        cmd.Parameters.AddWithValue("@source",
            string.IsNullOrEmpty(node.Source) ? "env" : node.Source);
    }
}
