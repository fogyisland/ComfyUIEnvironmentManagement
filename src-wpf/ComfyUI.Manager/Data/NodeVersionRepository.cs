using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// NodeVersionRepository:CRUD for the <c>node_versions</c> table。
/// key: (node_id, tag_name),value: 完整 VersionInfo。
/// 用于详情面板的版本历史下拉。
/// </summary>
public sealed class NodeVersionRepository
{
    private readonly CatalogCacheStore _store;

    public NodeVersionRepository(CatalogCacheStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 批量 upsert。一次 connection + transaction + prepared statement,
    /// 5837 × 10 ≈ 6 万行 ~秒级完成。先按 (node_id, tag_name) DELETE 旧
    /// 的再 INSERT(避免 UNIQUE 冲突;版本数据小、覆盖可接受)。
    /// </summary>
    public int UpsertBatch(IEnumerable<(string NodeId, VersionInfo Version)> items)
    {
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM node_versions WHERE node_id = @nid";
            del.Parameters.Add("@nid", SqliteType.Text);
            del.Prepare();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO node_versions
                (node_id, tag_name, published_at, is_prerelease, fetched_at)
            VALUES
                (@nid, @tag, @pub, @pre, @fetch)";
        cmd.Parameters.Add("@nid", SqliteType.Text);
        cmd.Parameters.Add("@tag", SqliteType.Text);
        cmd.Parameters.Add("@pub", SqliteType.Text);
        cmd.Parameters.Add("@pre", SqliteType.Integer);
        cmd.Parameters.Add("@fetch", SqliteType.Text);
        cmd.Prepare();

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        int count = 0;
        // 按 node_id 分组,每个 node_id 只 DELETE 一次
        string? lastNid = null;
        foreach (var (nid, v) in items)
        {
            if (nid != lastNid)
            {
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM node_versions WHERE node_id = @nid";
                del.Parameters.AddWithValue("@nid", nid);
                del.ExecuteNonQuery();
                lastNid = nid;
            }
            cmd.Parameters["@nid"].Value = nid;
            cmd.Parameters["@tag"].Value = v.Tag;
            cmd.Parameters["@pub"].Value = v.PublishedAt;
            cmd.Parameters["@pre"].Value = v.IsPrerelease ? 1 : 0;
            cmd.Parameters["@fetch"].Value = now;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }

    /// <summary>
    /// 取一个节点的所有版本,按发布时间倒序(最新在前)。
    /// </summary>
    public List<VersionInfo> ListByNode(string nodeId)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tag_name, published_at, is_prerelease
            FROM node_versions
            WHERE node_id = @nid
            ORDER BY published_at DESC";
        cmd.Parameters.AddWithValue("@nid", nodeId);
        using var reader = cmd.ExecuteReader();
        var list = new List<VersionInfo>();
        while (reader.Read())
        {
            list.Add(new VersionInfo
            {
                Tag = reader.GetString(0),
                PublishedAt = reader.GetString(1),
                IsPrerelease = reader.GetInt32(2) != 0,
            });
        }
        return list;
    }
}
