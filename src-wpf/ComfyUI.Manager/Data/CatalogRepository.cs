using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// SELECT 列清单——Search / ListNonExpired 共用,防止跟 <see cref="Read"/>
    /// 的列索引漂移(v0.6.7.4 加 typed 列时 ListNonExpired 漏改导致越界)。
    /// </summary>
    private const string CatalogCacheColumns =
        "id, source_url, package, raw_metadata, cached_at, expires_at, " +
        "latest_version, author, description, install_type, reference, last_update, pip_json, " +
        "license, tags_json, stars, downloads, last_commit, readme_markdown, " +
        "latest_changelog, deprecated, python_compat_json, os_compat_json, metadata_fetched_at";

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
            SELECT " + CatalogCacheColumns + @"
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
            SELECT " + CatalogCacheColumns + @"
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
        cmd.Parameters.Add("@author", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@description", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@install_type", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@reference", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@last_update", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@pip_json", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@license", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@tags_json", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@stars", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@downloads", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@last_commit", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@readme_markdown", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@latest_changelog", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@deprecated", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.Add("@python_compat_json", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@os_compat_json", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@metadata_fetched_at", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Prepare();
        int count = 0;
        foreach (var entry in entries)
        {
            var typed = ExtractTypedFields(entry);
            cmd.Parameters["@id"].Value = entry.Id;
            cmd.Parameters["@source_url"].Value = entry.SourceUrl;
            cmd.Parameters["@package"].Value = entry.Package;
            cmd.Parameters["@raw_metadata"].Value =
                JsonSerializer.Serialize(entry.RawMetadata, JsonOptions);
            cmd.Parameters["@cached_at"].Value = entry.CachedAt;
            cmd.Parameters["@expires_at"].Value = entry.ExpiresAt;
            cmd.Parameters["@author"].Value = (object?)typed.author ?? DBNull.Value;
            cmd.Parameters["@description"].Value = (object?)typed.description ?? DBNull.Value;
            cmd.Parameters["@install_type"].Value = (object?)typed.installType ?? DBNull.Value;
            cmd.Parameters["@reference"].Value = (object?)typed.reference ?? DBNull.Value;
            cmd.Parameters["@last_update"].Value = (object?)typed.lastUpdate ?? DBNull.Value;
            cmd.Parameters["@pip_json"].Value = typed.pipJson;
            cmd.Parameters["@license"].Value = (object?)entry.License ?? DBNull.Value;
            cmd.Parameters["@tags_json"].Value = SerializeStringArray(entry.Tags);
            cmd.Parameters["@stars"].Value = entry.Stars;
            cmd.Parameters["@downloads"].Value = entry.Downloads;
            cmd.Parameters["@last_commit"].Value = (object?)entry.LastCommit ?? DBNull.Value;
            cmd.Parameters["@readme_markdown"].Value = (object?)entry.ReadmeMarkdown ?? DBNull.Value;
            cmd.Parameters["@latest_changelog"].Value = (object?)entry.LatestChangelog ?? DBNull.Value;
            cmd.Parameters["@deprecated"].Value = entry.Deprecated ? 1 : 0;
            cmd.Parameters["@python_compat_json"].Value = SerializeStringArray(entry.PythonCompat);
            cmd.Parameters["@os_compat_json"].Value = SerializeStringArray(entry.OsCompat);
            cmd.Parameters["@metadata_fetched_at"].Value = (object?)entry.MetadataFetchedAt ?? DBNull.Value;
            cmd.ExecuteNonQuery();
            count++;
            onUpserted?.Invoke(entry);
        }
        tx.Commit();
        return count;
    }

    private static (string? author, string? description, string? installType,
                    string? reference, string? lastUpdate, string pipJson)
    ExtractTypedFields(CatalogEntry entry)
    {
        var rm = entry.RawMetadata ?? new Dictionary<string, object?>();
        string? Get(string k) => rm.TryGetValue(k, out var v) ? v?.ToString() : null;

        // pip 字段有多种形态:
        //  - List<object?>            —— 刚 fetch 完(CatalogFetcher.ConvertJsonValue 递归转换过)
        //  - JsonElement(Array)      —— SQLite 往返后(raw_metadata 反序列化成 object? 就是 JsonElement)
        //  - IEnumerable<object?>     —— 防御性(其他集合类型)
        //  - string                   —— 防御性(逗号分隔的老数据)
        var pipList = new List<string?>();
        if (rm.TryGetValue("pip", out var p))
        {
            if (p is List<object?> pl)
            {
                foreach (var item in pl)
                {
                    if (item is not null) pipList.Add(item.ToString());
                }
            }
            else if (p is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (s is not null) pipList.Add(s);
                    }
                }
            }
            else if (p is IEnumerable<object?> eo)
            {
                foreach (var item in eo)
                {
                    if (item is not null) pipList.Add(item.ToString());
                }
            }
            else if (p is string ps)
            {
                foreach (var part in ps.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    pipList.Add(part);
                }
            }
        }

        var reqs = PipRequirement.ParseList(pipList);
        var pipJson = JsonSerializer.Serialize(
            reqs.Select(r => new { name = r.Name, spec = r.Specifier }),
            JsonOptions);
        return (Get("author"), Get("description"), Get("install_type"),
                Get("reference"), Get("last_update"), pipJson);
    }

    private const string UpsertCommandText = @"
        INSERT INTO catalog_cache
            (id, source_url, package, raw_metadata, cached_at, expires_at,
             author, description, install_type, reference, last_update, pip_json,
             license, tags_json, stars, downloads, last_commit, readme_markdown,
             latest_changelog, deprecated, python_compat_json, os_compat_json, metadata_fetched_at)
        VALUES
            (@id, @source_url, @package, @raw_metadata, @cached_at, @expires_at,
             @author, @description, @install_type, @reference, @last_update, @pip_json,
             @license, @tags_json, @stars, @downloads, @last_commit, @readme_markdown,
             @latest_changelog, @deprecated, @python_compat_json, @os_compat_json, @metadata_fetched_at)
        ON CONFLICT(source_url, package) DO UPDATE SET
            raw_metadata=excluded.raw_metadata,
            cached_at=excluded.cached_at,
            expires_at=excluded.expires_at,
            author=excluded.author,
            description=excluded.description,
            install_type=excluded.install_type,
            reference=excluded.reference,
            last_update=excluded.last_update,
            pip_json=excluded.pip_json,
            license=excluded.license,
            tags_json=excluded.tags_json,
            stars=excluded.stars,
            downloads=excluded.downloads,
            last_commit=excluded.last_commit,
            readme_markdown=excluded.readme_markdown,
            latest_changelog=excluded.latest_changelog,
            deprecated=excluded.deprecated,
            python_compat_json=excluded.python_compat_json,
            os_compat_json=excluded.os_compat_json,
            metadata_fetched_at=excluded.metadata_fetched_at";

    private static void BindUpsertParameters(SqliteCommand cmd, CatalogEntry entry)
    {
        var typed = ExtractTypedFields(entry);
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@source_url", entry.SourceUrl);
        cmd.Parameters.AddWithValue("@package", entry.Package);
        cmd.Parameters.AddWithValue("@raw_metadata",
            JsonSerializer.Serialize(entry.RawMetadata, JsonOptions));
        cmd.Parameters.AddWithValue("@cached_at", entry.CachedAt);
        cmd.Parameters.AddWithValue("@expires_at", entry.ExpiresAt);
        cmd.Parameters.AddWithValue("@author", (object?)typed.author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)typed.description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@install_type", (object?)typed.installType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reference", (object?)typed.reference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_update", (object?)typed.lastUpdate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pip_json", typed.pipJson);
        cmd.Parameters.AddWithValue("@license", (object?)entry.License ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags_json", SerializeStringArray(entry.Tags));
        cmd.Parameters.AddWithValue("@stars", entry.Stars);
        cmd.Parameters.AddWithValue("@downloads", entry.Downloads);
        cmd.Parameters.AddWithValue("@last_commit", (object?)entry.LastCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@readme_markdown", (object?)entry.ReadmeMarkdown ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@latest_changelog", (object?)entry.LatestChangelog ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deprecated", entry.Deprecated ? 1 : 0);
        cmd.Parameters.AddWithValue("@python_compat_json", SerializeStringArray(entry.PythonCompat));
        cmd.Parameters.AddWithValue("@os_compat_json", SerializeStringArray(entry.OsCompat));
        cmd.Parameters.AddWithValue("@metadata_fetched_at", (object?)entry.MetadataFetchedAt ?? DBNull.Value);
    }

    /// <summary>
    /// 批量 UPDATE latest_version。一次 connection + transaction,5000+ 条
    /// ~几百毫秒。items 中 null value 跳过(不更新)。
    /// </summary>
    public int UpdateLatestVersions(IEnumerable<(string Id, string Version)> items)
    {
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE catalog_cache SET latest_version = @v WHERE id = @id";
        cmd.Parameters.Add("@v", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
        cmd.Prepare();
        int count = 0;
        foreach (var (id, ver) in items)
        {
            if (string.IsNullOrEmpty(ver)) continue;
            cmd.Parameters["@v"].Value = ver;
            cmd.Parameters["@id"].Value = id;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }

    private static string SerializeStringArray(IReadOnlyList<string> list)
    {
        if (list is null || list.Count == 0) return "[]";
        return JsonSerializer.Serialize(list, JsonOptions);
    }

    private static IReadOnlyList<string> ParseStringArray(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return Array.Empty<string>();
        var json = r.GetString(i);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions)
                ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static CatalogEntry Read(SqliteDataReader reader)
    {
        var rawJson = reader.GetString(3);
        var pipJson = reader.IsDBNull(12) ? "" : reader.GetString(12);
        var reqs = TryParsePipRequirements(pipJson);
        return new CatalogEntry
        {
            Id = reader.GetString(0),
            SourceUrl = reader.GetString(1),
            Package = reader.GetString(2),
            RawMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                rawJson, JsonOptions) ?? new Dictionary<string, object?>(),
            CachedAt = reader.GetString(4),
            ExpiresAt = reader.GetString(5),
            LatestVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
            Author = reader.IsDBNull(7) ? null : reader.GetString(7),
            Description = reader.IsDBNull(8) ? null : reader.GetString(8),
            InstallType = reader.IsDBNull(9) ? null : reader.GetString(9),
            Reference = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastUpdate = reader.IsDBNull(11) ? null : reader.GetString(11),
            PipRequirements = reqs,
            License = reader.IsDBNull(13) ? null : reader.GetString(13),
            Tags = ParseStringArray(reader, 14),
            Stars = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
            Downloads = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
            LastCommit = reader.IsDBNull(17) ? null : reader.GetString(17),
            ReadmeMarkdown = reader.IsDBNull(18) ? null : reader.GetString(18),
            LatestChangelog = reader.IsDBNull(19) ? null : reader.GetString(19),
            Deprecated = !reader.IsDBNull(20) && reader.GetInt32(20) != 0,
            PythonCompat = ParseStringArray(reader, 21),
            OsCompat = ParseStringArray(reader, 22),
            MetadataFetchedAt = reader.IsDBNull(23) ? null : reader.GetString(23),
        };
    }

    private static IReadOnlyList<PipRequirement> TryParsePipRequirements(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PipRequirement>();
        try
        {
            var rows = JsonSerializer.Deserialize<List<RawPipRow>>(json, JsonOptions);
            if (rows is null) return Array.Empty<PipRequirement>();
            return rows.Select(r => new PipRequirement(r.name ?? "", r.spec)).ToList();
        }
        catch
        {
            return Array.Empty<PipRequirement>();
        }
    }

    private sealed class RawPipRow
    {
        public string? name { get; set; }
        public string? spec { get; set; }
    }
}
