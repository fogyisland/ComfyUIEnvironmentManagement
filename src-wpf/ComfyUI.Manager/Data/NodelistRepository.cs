using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-redesign:NodesList + Nodesdetail 仓库
/// 
/// 数据流:
/// 1. 下载 default json(lttdrdata/ComfyUI-Manager/main/custom-node-list.json)
/// 2. 解析 → List&lt;(author, repo_name)&gt;
/// 3. UpsertNodesList(增量入库或完整入库)
/// 4. 调 NodeRepoQueryService 拿每个 (author, repo_name) metadata
/// 5. 写 Nodesdetail(一对多)
/// 
/// Schema 见 SqliteConnectionFactory.Schema(nodelist_entries + nodelist_details)
/// </summary>
public sealed class NodelistRepository
{
    private readonly SqliteConnectionFactory _factory;

    public NodelistRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public sealed record Entry(
        string Author,
        string RepoName,
        string FirstSeenAt,
        string? LastIngestedAt,
        string Source);

    public sealed record Detail(
        string Author,
        string RepoName,
        string Version,
        string? Description,
        int? Stars,
        int? Watchers,
        string? License,
        string? DefaultBranch,
        string? UpdatedAt,
        string? RawJson,
        string Host,
        string FetchedAt);

    public void UpsertEntry(string author, string repoName, string source)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodelist_entries (author, repo_name, first_seen_at, last_ingested_at, source)
            VALUES (@author, @repo, @now, @now, @source)
            ON CONFLICT(author, repo_name) DO UPDATE SET
                last_ingested_at = excluded.last_ingested_at";
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@source", source);
        cmd.ExecuteNonQuery();
    }

    public void UpsertDetail(Detail d)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodelist_details
                (author, repo_name, version, description, stars, watchers, license,
                 default_branch, updated_at, raw_json, host, fetched_at)
            VALUES (@author, @repo, @ver, @desc, @stars, @watchers, @lic,
                    @branch, @updated, @raw, @host, @now)
            ON CONFLICT(author, repo_name, version) DO UPDATE SET
                description = excluded.description,
                stars = excluded.stars,
                watchers = excluded.watchers,
                license = excluded.license,
                default_branch = excluded.default_branch,
                updated_at = excluded.updated_at,
                raw_json = excluded.raw_json,
                host = excluded.host,
                fetched_at = excluded.fetched_at";
        cmd.Parameters.AddWithValue("@author", d.Author);
        cmd.Parameters.AddWithValue("@repo", d.RepoName);
        cmd.Parameters.AddWithValue("@ver", d.Version);
        cmd.Parameters.AddWithValue("@desc", (object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stars", (object?)d.Stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@watchers", (object?)d.Watchers ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lic", (object?)d.License ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@branch", (object?)d.DefaultBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updated", (object?)d.UpdatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw", (object?)d.RawJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@host", d.Host);
        cmd.Parameters.AddWithValue("@now", d.FetchedAt);
        cmd.ExecuteNonQuery();
    }

    public List<Entry> GetAllEntries()
    {
        var list = new List<Entry>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT author, repo_name, first_seen_at, last_ingested_at, source FROM nodelist_entries ORDER BY author, repo_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Entry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }
        return list;
    }

    public List<Detail> GetDetailsByAuthor(string author, string repoName)
    {
        var list = new List<Detail>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT author, repo_name, version, description, stars, watchers, license,
                default_branch, updated_at, raw_json, host, fetched_at
            FROM nodelist_details WHERE author = @a AND repo_name = @r
            ORDER BY version DESC";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Detail(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11)));
        }
        return list;
    }

    public HashSet<(string Author, string Repo)> GetExistingKeys()
    {
        var set = new HashSet<(string, string)>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT author, repo_name FROM nodelist_entries";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add((reader.GetString(0), reader.GetString(1)));
        }
        return set;
    }
}
