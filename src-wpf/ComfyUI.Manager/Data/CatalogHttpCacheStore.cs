using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.14: 存/取 ETag + Last-Modified per source URL,让 <see cref="CatalogFetcher"/>
/// 发 If-None-Match / If-Modified-Since 走 HTTP cache。同 DB 原子事务,不另开 JSON 文件。
/// 表 schema 由 <see cref="CatalogCacheStore"/> 在 EnsureCatalogCacheDbSchema 中创建(T3)。
/// 非 sealed:T6 的 FakeCatalogHttpCacheStore 需要 subclass override(v0.6.13-B.1 lesson)。
/// </summary>
public class CatalogHttpCacheStore
{
    private readonly string _dbPath;
    private readonly AppLogger? _logger;

    public CatalogHttpCacheStore(string dbPath, AppLogger? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    public CatalogHttpCacheStore()
        : this(Path.Combine(AppContext.BaseDirectory, "Data", "catalog-cache.db"))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public virtual async Task<(string? Etag, string? LastModified)> GetAsync(
        string url, CancellationToken ct = default)
    {
        try
        {
            using var conn = OpenConn();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT etag, last_modified FROM catalog_http_cache WHERE url = @url";
            cmd.Parameters.AddWithValue("@url", url);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var etag = reader.IsDBNull(0) ? null : reader.GetString(0);
                var lastMod = reader.IsDBNull(1) ? null : reader.GetString(1);
                return (etag, lastMod);
            }
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger?.Warn("catalog-http-cache",
                $"GetAsync 异常 url={url} reason={ex.Message}");
            return (null, null);  // 损坏回退:无 etag → 下次 fetch 走全量
        }
    }

    public virtual async Task PutAsync(string url, string? etag, string? lastModified,
        CancellationToken ct = default)
    {
        using var conn = OpenConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO catalog_http_cache (url, etag, last_modified, fetched_at)
            VALUES (@url, @etag, @lastmod, @fetchedAt)
            ON CONFLICT(url) DO UPDATE SET
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                fetched_at = excluded.fetched_at";
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastmod", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fetchedAt",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection OpenConn()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
