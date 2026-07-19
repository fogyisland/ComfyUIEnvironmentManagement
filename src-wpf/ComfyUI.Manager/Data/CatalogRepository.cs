using System;
using System.Collections.Generic;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// CatalogRepository:CRUD for the <c>catalog_cache</c> table.
/// </summary>
public sealed class CatalogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CatalogCacheStore _store;

    public CatalogRepository(CatalogCacheStore store)
    {
        _store = store;
    }

    public List<CatalogEntry> Search(string query, int limit)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        // limit <= 0 means "no LIMIT clause" (SQLite would treat LIMIT 0 as
        // empty result set otherwise).
        cmd.CommandText = @"
            SELECT id, source_url, package, raw_metadata, cached_at, expires_at
            FROM catalog_cache
            WHERE LOWER(package) LIKE @pattern
               OR LOWER(raw_metadata) LIKE @pattern
            ORDER BY package"
            + (limit > 0 ? " LIMIT @limit" : "");
        cmd.Parameters.AddWithValue("@pattern", $"%{query.ToLowerInvariant()}%");
        if (limit > 0) cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<CatalogEntry>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public List<CatalogEntry> ListNonExpired(DateTime nowUtc)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, source_url, package, raw_metadata, cached_at, expires_at
            FROM catalog_cache
            WHERE expires_at > @now
            ORDER BY package";
        // ISO 8601 naive UTC; Python side writes naive local time but writes
        // and reads go through the same pipeline, so naive compare is fine.
        cmd.Parameters.AddWithValue("@now", nowUtc.ToString("yyyy-MM-ddTHH:mm:ss"));
        using var reader = cmd.ExecuteReader();
        var list = new List<CatalogEntry>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public void Upsert(CatalogEntry entry)
    {
        using var conn = _store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertCommandText;
        BindUpsertParameters(cmd, entry);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Batched Upsert: 跑一次 connection 开启 + 一次 transaction commit,
    /// 比逐条 Upsert 快 10-50x(后者每条都重新打开 connection + 写 WAL)。
    /// 每条 INSERT 后同步调 <paramref name="onUpserted"/>(后台线程,UI 端
    /// 用 Progress&lt;CatalogEntry&gt; 自动 marshal)。
    /// 返回成功 INSERT 的条数。
    /// </summary>
    public int UpsertBatch(IEnumerable<CatalogEntry> entries, Action<CatalogEntry>? onUpserted = null)
    {
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertCommandText;
        // pre-add named parameters once, mutate .Value per row (avoids re-parsing)
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@source_url", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@package", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@raw_metadata", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@cached_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@expires_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Prepare();
        int count = 0;
        foreach (var entry in entries)
        {
            cmd.Parameters["@id"].Value = entry.Id;
            cmd.Parameters["@source_url"].Value = entry.SourceUrl;
            cmd.Parameters["@package"].Value = entry.Package;
            cmd.Parameters["@raw_metadata"].Value =
                JsonSerializer.Serialize(entry.RawMetadata, JsonOptions);
            cmd.Parameters["@cached_at"].Value = entry.CachedAt;
            cmd.Parameters["@expires_at"].Value = entry.ExpiresAt;
            cmd.ExecuteNonQuery();
            count++;
            onUpserted?.Invoke(entry);
        }
        tx.Commit();
        return count;
    }

    private const string UpsertCommandText = @"
        INSERT INTO catalog_cache
            (id, source_url, package, raw_metadata, cached_at, expires_at)
        VALUES
            (@id, @source_url, @package, @raw_metadata, @cached_at, @expires_at)
        ON CONFLICT(source_url, package) DO UPDATE SET
            raw_metadata=excluded.raw_metadata,
            cached_at=excluded.cached_at,
            expires_at=excluded.expires_at";

    private static void BindUpsertParameters(SqliteCommand cmd, CatalogEntry entry)
    {
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@source_url", entry.SourceUrl);
        cmd.Parameters.AddWithValue("@package", entry.Package);
        cmd.Parameters.AddWithValue("@raw_metadata",
            JsonSerializer.Serialize(entry.RawMetadata, JsonOptions));
        cmd.Parameters.AddWithValue("@cached_at", entry.CachedAt);
        cmd.Parameters.AddWithValue("@expires_at", entry.ExpiresAt);
    }

    private static CatalogEntry Read(SqliteDataReader reader)
    {
        var rawJson = reader.GetString(3);
        return new CatalogEntry
        {
            Id = reader.GetString(0),
            SourceUrl = reader.GetString(1),
            Package = reader.GetString(2),
            RawMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                rawJson, JsonOptions) ?? new Dictionary<string, object?>(),
            CachedAt = reader.GetString(4),
            ExpiresAt = reader.GetString(5),
        };
    }
}
