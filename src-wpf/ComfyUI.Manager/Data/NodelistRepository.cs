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
        string Source,
        // v1.0.0.x (2026-09-15) T43h+user:raw JSON 全量入库(22 字段)。
        // 字符串/数字直接存;数组(pip/tags/preemptions/files/badges)/对象(dependencies)
        // 走 JsonSerializer.Serialize 存 JSON 字符串。Ingestor 在 line 100 阶段 ParseEntry。
        string? Id,
        string? Reference,
        string? Reference2,
        string? FilesJson,
        string? InstallType,
        string? PipJson,
        string? AptDependency,
        string? DependenciesJson,
        string? PreemptionsJson,
        string? NodenamePattern,
        string? Nickname,
        string? Category,
        string? TagsJson,
        string? LastUpdate,
        int? RawStars,
        string? BadgesJson,
        string? JsPath,
        string? RawLicense);

    public sealed record Detail(
        string Author,
        string RepoName,
        string Version,
        string? Description,
        int? Stars,
        int? Watchers,
        int? Forks,
        string? License,
        string? DefaultBranch,
        string? UpdatedAt,
        string? PushedAt,
        string? HtmlUrl,
        string? Language,
        int? OpenIssues,
        // raw_json 里的 topics[] (e.g. ["image","controlnet"]) — 用 string 比 JSON 数组更方便 XAML binding。
        // 写入:Ingestor 解析 meta.topics 数组并 string join / 拆分;读取:VM 拿来做 tag 显示。
        // v1.0.0.x T43f+user:右边详情加 tags 显示(用户原话"右边需要按照更加详细的内容列出")。
        string? Topics,
        string? RawJson,
        string Host,
        string FetchedAt);

    public void UpsertEntry(string author, string repoName, string source)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        // v1.0.0.x T43h+user:全量字段入库 — ON CONFLICT 时只更新 last_ingested_at 和
        // 18 个新 raw JSON 列(不覆盖 first_seen_at,因为这是首次入库时间戳)。
        cmd.CommandText = @"
            INSERT INTO nodelist_entries
                (author, repo_name, first_seen_at, last_ingested_at, source,
                 id, reference, reference2, files_json, install_type,
                 pip_json, apt_dependency, dependencies_json, preemptions_json,
                 nodename_pattern, nickname, category, tags_json,
                 last_update, raw_stars, badges_json, js_path, raw_license)
            VALUES
                (@author, @repo, @now, @now, @source,
                 @id, @reference, @reference2, @filesJson, @installType,
                 @pipJson, @aptDependency, @dependenciesJson, @preemptionsJson,
                 @nodenamePattern, @nickname, @category, @tagsJson,
                 @lastUpdate, @rawStars, @badgesJson, @jsPath, @rawLicense)
            ON CONFLICT(author, repo_name) DO UPDATE SET
                last_ingested_at = excluded.last_ingested_at,
                id = excluded.id,
                reference = excluded.reference,
                reference2 = excluded.reference2,
                files_json = excluded.files_json,
                install_type = excluded.install_type,
                pip_json = excluded.pip_json,
                apt_dependency = excluded.apt_dependency,
                dependencies_json = excluded.dependencies_json,
                preemptions_json = excluded.preemptions_json,
                nodename_pattern = excluded.nodename_pattern,
                nickname = excluded.nickname,
                category = excluded.category,
                tags_json = excluded.tags_json,
                last_update = excluded.last_update,
                raw_stars = excluded.raw_stars,
                badges_json = excluded.badges_json,
                js_path = excluded.js_path,
                raw_license = excluded.raw_license";
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@source", source);
        // v1.0.0.x T43h:18 个新 raw JSON 列。null 入 DBNull(老 DB 未填时为 NULL)。
        // 入参由 Ingestor 在 line 100 ParseEntry 时填充。这里留默认 null —
        // 调用方(ProcessOneAsync)用 18 参数重载,这里只覆盖原 (author, repo, source) 三参入口。
        cmd.Parameters.AddWithValue("@id", DBNull.Value);
        cmd.Parameters.AddWithValue("@reference", DBNull.Value);
        cmd.Parameters.AddWithValue("@reference2", DBNull.Value);
        cmd.Parameters.AddWithValue("@filesJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@installType", DBNull.Value);
        cmd.Parameters.AddWithValue("@pipJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@aptDependency", DBNull.Value);
        cmd.Parameters.AddWithValue("@dependenciesJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@preemptionsJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@nodenamePattern", DBNull.Value);
        cmd.Parameters.AddWithValue("@nickname", DBNull.Value);
        cmd.Parameters.AddWithValue("@category", DBNull.Value);
        cmd.Parameters.AddWithValue("@tagsJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@lastUpdate", DBNull.Value);
        cmd.Parameters.AddWithValue("@rawStars", DBNull.Value);
        cmd.Parameters.AddWithValue("@badgesJson", DBNull.Value);
        cmd.Parameters.AddWithValue("@jsPath", DBNull.Value);
        cmd.Parameters.AddWithValue("@rawLicense", DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// v1.0.0.x T43h+user:全量入库 UpsertEntry — Ingestor 解析 raw JSON 24 字段后调用。
    /// 18 个新列由 caller 提供(数组/对象已 JsonSerializer.Serialize 成字符串)。
    /// 老 DB backfill 时这 18 列为 NULL(由 EnsureColumn 加,初始无值)。
    /// </summary>
    public void UpsertEntry(
        string author, string repoName, string source,
        string? id, string? reference, string? reference2, string? filesJson,
        string? installType, string? pipJson, string? aptDependency,
        string? dependenciesJson, string? preemptionsJson,
        string? nodenamePattern, string? nickname, string? category,
        string? tagsJson, string? lastUpdate, int? rawStars,
        string? badgesJson, string? jsPath, string? rawLicense)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodelist_entries
                (author, repo_name, first_seen_at, last_ingested_at, source,
                 id, reference, reference2, files_json, install_type,
                 pip_json, apt_dependency, dependencies_json, preemptions_json,
                 nodename_pattern, nickname, category, tags_json,
                 last_update, raw_stars, badges_json, js_path, raw_license)
            VALUES
                (@author, @repo, @now, @now, @source,
                 @id, @reference, @reference2, @filesJson, @installType,
                 @pipJson, @aptDependency, @dependenciesJson, @preemptionsJson,
                 @nodenamePattern, @nickname, @category, @tagsJson,
                 @lastUpdate, @rawStars, @badgesJson, @jsPath, @rawLicense)
            ON CONFLICT(author, repo_name) DO UPDATE SET
                last_ingested_at = excluded.last_ingested_at,
                id = excluded.id,
                reference = excluded.reference,
                reference2 = excluded.reference2,
                files_json = excluded.files_json,
                install_type = excluded.install_type,
                pip_json = excluded.pip_json,
                apt_dependency = excluded.apt_dependency,
                dependencies_json = excluded.dependencies_json,
                preemptions_json = excluded.preemptions_json,
                nodename_pattern = excluded.nodename_pattern,
                nickname = excluded.nickname,
                category = excluded.category,
                tags_json = excluded.tags_json,
                last_update = excluded.last_update,
                raw_stars = excluded.raw_stars,
                badges_json = excluded.badges_json,
                js_path = excluded.js_path,
                raw_license = excluded.raw_license";
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reference", (object?)reference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reference2", (object?)reference2 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filesJson", (object?)filesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@installType", (object?)installType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pipJson", (object?)pipJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aptDependency", (object?)aptDependency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dependenciesJson", (object?)dependenciesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@preemptionsJson", (object?)preemptionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nodenamePattern", (object?)nodenamePattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nickname", (object?)nickname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tagsJson", (object?)tagsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastUpdate", (object?)lastUpdate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rawStars", (object?)rawStars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@badgesJson", (object?)badgesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@jsPath", (object?)jsPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rawLicense", (object?)rawLicense ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpsertDetail(Detail d)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodelist_details
                (author, repo_name, version, description, stars, watchers, forks, license,
                 default_branch, updated_at, pushed_at, html_url, language, open_issues,
                 topics, raw_json, host, fetched_at)
            VALUES (@author, @repo, @ver, @desc, @stars, @watchers, @forks, @lic,
                    @branch, @updated, @pushed, @url, @lang, @issues,
                    @topics, @raw, @host, @now)
            ON CONFLICT(author, repo_name, version) DO UPDATE SET
                description = excluded.description,
                stars = excluded.stars,
                watchers = excluded.watchers,
                forks = excluded.forks,
                license = excluded.license,
                default_branch = excluded.default_branch,
                updated_at = excluded.updated_at,
                pushed_at = excluded.pushed_at,
                html_url = excluded.html_url,
                language = excluded.language,
                open_issues = excluded.open_issues,
                topics = excluded.topics,
                raw_json = excluded.raw_json,
                host = excluded.host,
                fetched_at = excluded.fetched_at";
        cmd.Parameters.AddWithValue("@author", d.Author);
        cmd.Parameters.AddWithValue("@repo", d.RepoName);
        cmd.Parameters.AddWithValue("@ver", d.Version);
        cmd.Parameters.AddWithValue("@desc", (object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stars", (object?)d.Stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@watchers", (object?)d.Watchers ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@forks", (object?)d.Forks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lic", (object?)d.License ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@branch", (object?)d.DefaultBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updated", (object?)d.UpdatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pushed", (object?)d.PushedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)d.HtmlUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lang", (object?)d.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@issues", (object?)d.OpenIssues ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@topics", (object?)d.Topics ?? DBNull.Value);
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
        // v1.0.0.x T43h+user:SELECT 23 列(5 旧 + 18 新 raw JSON 字段)。
        // 老 DB backfill 后新列全 NULL,Read 通过 IsDBNull 兜底返回 null。
        cmd.CommandText = @"SELECT author, repo_name, first_seen_at, last_ingested_at, source,
                id, reference, reference2, files_json, install_type,
                pip_json, apt_dependency, dependencies_json, preemptions_json,
                nodename_pattern, nickname, category, tags_json,
                last_update, raw_stars, badges_json, js_path, raw_license
            FROM nodelist_entries ORDER BY author, repo_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Entry(
                reader.GetString(0),                                     // author
                reader.GetString(1),                                     // repo_name
                reader.GetString(2),                                     // first_seen_at
                reader.IsDBNull(3) ? null : reader.GetString(3),         // last_ingested_at
                reader.GetString(4),                                     // source
                reader.IsDBNull(5) ? null : reader.GetString(5),         // id
                reader.IsDBNull(6) ? null : reader.GetString(6),         // reference
                reader.IsDBNull(7) ? null : reader.GetString(7),         // reference2
                reader.IsDBNull(8) ? null : reader.GetString(8),         // files_json
                reader.IsDBNull(9) ? null : reader.GetString(9),         // install_type
                reader.IsDBNull(10) ? null : reader.GetString(10),       // pip_json
                reader.IsDBNull(11) ? null : reader.GetString(11),       // apt_dependency
                reader.IsDBNull(12) ? null : reader.GetString(12),       // dependencies_json
                reader.IsDBNull(13) ? null : reader.GetString(13),       // preemptions_json
                reader.IsDBNull(14) ? null : reader.GetString(14),       // nodename_pattern
                reader.IsDBNull(15) ? null : reader.GetString(15),       // nickname
                reader.IsDBNull(16) ? null : reader.GetString(16),       // category
                reader.IsDBNull(17) ? null : reader.GetString(17),       // tags_json
                reader.IsDBNull(18) ? null : reader.GetString(18),       // last_update
                reader.IsDBNull(19) ? null : reader.GetInt32(19),        // raw_stars
                reader.IsDBNull(20) ? null : reader.GetString(20),       // badges_json
                reader.IsDBNull(21) ? null : reader.GetString(21),       // js_path
                reader.IsDBNull(22) ? null : reader.GetString(22)));     // raw_license
        }
        return list;
    }

    public List<Detail> GetDetailsByAuthor(string author, string repoName)
    {
        var list = new List<Detail>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        // v1.0.0.x T43f+user:右边详情只显示最新 10 个 version(用户原话"右边需要按照更加详细的内容列出")。
        // ORDER BY COALESCE(pushed_at, updated_at, fetched_at) DESC — pushed_at 最贴近"最近 commit",
        // 兜底 updated_at / fetched_at;老 DB 没 pushed_at 列会变 NULL,COALESCE 接续。
        cmd.CommandText = @"SELECT author, repo_name, version, description, stars, watchers, forks, license,
                default_branch, updated_at, pushed_at, html_url, language, open_issues,
                topics, raw_json, host, fetched_at
            FROM nodelist_details WHERE author = @a AND repo_name = @r
            ORDER BY COALESCE(pushed_at, updated_at, fetched_at) DESC
            LIMIT 10";
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
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetString(16),
                reader.GetString(17)));
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
