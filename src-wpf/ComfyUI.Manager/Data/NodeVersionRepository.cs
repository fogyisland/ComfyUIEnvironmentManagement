using System;
using System.Collections.Generic;
using System.Linq;
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
    /// v0.6.14 hotfix: 批量 upsert 接受 (source_url, package, VersionInfo) 而非
    /// (node_id, VersionInfo)。原因:catalog_cache.id 是 CatalogFetcher 每次 fetch
    /// 都新分配的 Guid.NewGuid(),跨 refresh 不稳定;但 (source_url, package) 是
    /// schema UNIQUE 约束,稳定。repository 内部从 catalog_cache 解析出真正的
    /// node_id 再写 node_versions —— 因为 (source_url, package) → node_id 的映射
    /// 在同一 connection 内一致。
    ///
    /// 一次 connection + transaction + prepared statement。5837 × 10 ≈ 6 万行
    /// ~秒级完成。按 (source_url, package) 分组,每个 group 只 DELETE 旧 versions
    /// 一次(避免 UNIQUE 冲突;版本数据小、覆盖可接受)。
    ///
    /// catalog_cache 里找不到对应 row 的 tuple 被静默跳过(例如 metadata refresh
    /// 在 catalog 之前发生,或 entry 已被硬删)。
    /// </summary>
    public int UpsertBatch(IEnumerable<(string SourceUrl, string Package, VersionInfo Version)> items)
    {
        // 1) 按 (source_url, package) 分组 → 拿每个 group 的 node_id
        var grouped = items
            .GroupBy(t => (t.SourceUrl, t.Package))
            .ToList();
        if (grouped.Count == 0) return 0;

        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();

        // 2) 解析 node_id:同 connection 内 SELECT WHERE (source_url=? AND package=?)
        var groupNodeIds = new Dictionary<(string, string), string>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText =
                "SELECT id FROM catalog_cache WHERE source_url = @s AND package = @p";
            sel.Parameters.Add("@s", SqliteType.Text);
            sel.Parameters.Add("@p", SqliteType.Text);
            sel.Transaction = tx;
            sel.Prepare();
            foreach (var g in grouped)
            {
                sel.Parameters["@s"].Value = g.Key.Item1;
                sel.Parameters["@p"].Value = g.Key.Item2;
                var nid = sel.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(nid))
                {
                    groupNodeIds[g.Key] = nid;
                }
            }
        }

        // 3) INSERT prepared statement
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
        string? lastNid = null;
        foreach (var g in grouped)
        {
            if (!groupNodeIds.TryGetValue(g.Key, out var nid)) continue;
            if (nid != lastNid)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM node_versions WHERE node_id = @nid";
                del.Parameters.AddWithValue("@nid", nid);
                del.ExecuteNonQuery();
                lastNid = nid;
            }
            foreach (var item in g)
            {
                var v = item.Version;
                cmd.Parameters["@nid"].Value = nid;
                cmd.Parameters["@tag"].Value = v.Tag;
                cmd.Parameters["@pub"].Value = v.PublishedAt;
                cmd.Parameters["@pre"].Value = v.IsPrerelease ? 1 : 0;
                cmd.Parameters["@fetch"].Value = now;
                cmd.ExecuteNonQuery();
                count++;
            }
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
