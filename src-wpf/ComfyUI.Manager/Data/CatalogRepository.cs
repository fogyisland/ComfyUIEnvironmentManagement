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
        cmd.CommandText = @"
            SELECT id, source_url, package, raw_metadata, cached_at, expires_at
            FROM catalog_cache
            WHERE LOWER(package) LIKE @pattern
               OR LOWER(raw_metadata) LIKE @pattern
            ORDER BY package
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@pattern", $"%{query.ToLowerInvariant()}%");
        cmd.Parameters.AddWithValue("@limit", limit);
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
        cmd.CommandText = @"
            INSERT INTO catalog_cache
                (id, source_url, package, raw_metadata, cached_at, expires_at)
            VALUES
                (@id, @source_url, @package, @raw_metadata, @cached_at, @expires_at)
            ON CONFLICT(source_url, package) DO UPDATE SET
                raw_metadata=excluded.raw_metadata,
                cached_at=excluded.cached_at,
                expires_at=excluded.expires_at";
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@source_url", entry.SourceUrl);
        cmd.Parameters.AddWithValue("@package", entry.Package);
        cmd.Parameters.AddWithValue("@raw_metadata",
            JsonSerializer.Serialize(entry.RawMetadata, JsonOptions));
        cmd.Parameters.AddWithValue("@cached_at", entry.CachedAt);
        cmd.Parameters.AddWithValue("@expires_at", entry.ExpiresAt);
        cmd.ExecuteNonQuery();
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
