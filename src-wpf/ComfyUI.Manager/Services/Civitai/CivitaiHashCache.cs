using System;
using System.IO;
using Microsoft.Data.Sqlite;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>
/// v1.0.0:SQLite-backed cache mapping (FilePath, SizeBytes, MtimeUtcTicks) → SHA256.
/// File metadata invalidates cache when file changes. Lives at
/// %APPDATA%/ComfyUI.Manager/civitai-hash-cache.sqlite. Path is independent of
/// state.db because hash data is non-sensitive and can be deleted freely.
///
/// Caller owns disposal. Schema:
///   CREATE TABLE file_hashes (
///     path TEXT NOT NULL,
///     size_bytes INTEGER NOT NULL,
///     mtime_utc_ticks INTEGER NOT NULL,
///     sha256 TEXT NOT NULL,
///     PRIMARY KEY (path, size_bytes, mtime_utc_ticks)
///   );
/// </summary>
public sealed class CivitaiHashCache : IDisposable
{
    private const string Schema = @"
        CREATE TABLE IF NOT EXISTS file_hashes (
            path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            mtime_utc_ticks INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            PRIMARY KEY (path, size_bytes, mtime_utc_ticks)
        );";

    private readonly SqliteConnection _conn;
    private readonly AppLogger? _logger;

    public CivitaiHashCache(string sqlitePath, AppLogger? logger = null)
    {
        if (string.IsNullOrEmpty(sqlitePath)) throw new ArgumentNullException(nameof(sqlitePath));

        var dir = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _logger = logger;
        _conn = new SqliteConnection($"Data Source={sqlitePath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    public string? Lookup(string filePath, long sizeBytes, long mtimeUtcTicks)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM file_hashes " +
                              "WHERE path = $p AND size_bytes = $s AND mtime_utc_ticks = $m LIMIT 1";
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$m", mtimeUtcTicks);
            var result = cmd.ExecuteScalar();
            return result is string s ? s : null;
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite lookup error: {ex.Message}");
            return null;
        }
    }

    public void Store(string filePath, long sizeBytes, long mtimeUtcTicks, string sha256)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO file_hashes (path, size_bytes, mtime_utc_ticks, sha256) " +
                              "VALUES ($p, $s, $m, $h)";
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$m", mtimeUtcTicks);
            cmd.Parameters.AddWithValue("$h", sha256);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite store error: {ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM file_hashes";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.Warn("civitai-hash-cache", $"⚠ SQLite clear error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
