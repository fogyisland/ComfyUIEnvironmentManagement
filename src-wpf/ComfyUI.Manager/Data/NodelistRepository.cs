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
        // 字符串/数字直接存;数组(pip/tags/preemptions/files)/对象(dependencies)
        // 走 JsonSerializer.Serialize 存 JSON 字符串。Ingestor 在 line 100 阶段 ParseEntry。
        // v1.0.0.x (2026-09-16) T43i.1+user 「把 author/title/reference/description 入库」:
        // description 也入库(用户原话"加上 author title reference description 字段")。
        // 跟 nodelist_details.description 区分(那个是 GitHub API metadata.description,
        // raw 是节点作者自填;两者并存,UI 优先显示 raw)。
        // v1.0.0.x (2026-09-16) T43i.2.2-fix:删 5 个 ≤0.017% 覆盖率列(AptDependency/
        // DependenciesJson/Nickname/LastUpdate/BadgesJson 各 1/5942 entry)。
        string? Id,
        string? Reference,
        string? Reference2,
        string? Description,        // T43i.1+user:raw JSON 顶层 description
        string? FilesJson,
        string? InstallType,
        string? PipJson,
        string? PreemptionsJson,
        string? NodenamePattern,
        string? Category,
        string? TagsJson,
        string? JsPath);

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
        // v1.0.0.x (2026-09-16) T43i.4+user「无变动不写入」:3 参简版不再用 ON CONFLICT 覆盖
        // 18 列 —— 直接调 12 参 UpsertEntry,所有 null 入 DBNull,12 参版本负责对比逻辑。
        cmd.CommandText = @"INSERT INTO nodelist_entries
                (author, repo_name, first_seen_at, last_ingested_at, source,
                 id, reference, reference2, description, files_json, install_type,
                 pip_json, preemptions_json, nodename_pattern, category,
                 tags_json, js_path)
            VALUES
                (@author, @repo, @now, @now, @source,
                 NULL, NULL, NULL, NULL, NULL, NULL,
                 NULL, NULL, NULL, NULL,
                 NULL, NULL)
            ON CONFLICT(author, repo_name) DO NOTHING";
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@source", source);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// v1.0.0.x T43h+user:全量入库 UpsertEntry — Ingestor 解析 raw JSON 24 字段后调用。
    /// 18 个新列由 caller 提供(数组/对象已 JsonSerializer.Serialize 成字符串)。
    /// v1.0.0.x T43i.1+user:加 description(19 列),raw JSON 顶层节点作者自填描述。
    /// 老 DB backfill 时这 19 列为 NULL(由 EnsureColumn 加,初始无值)。
    /// v1.0.0.x T43i.2.2-fix:5 个 ≤0.017% 覆盖率列(AptDependency/DependenciesJson/
    /// Nickname/LastUpdate/BadgesJson)从签名 + SQL/Parameters 中移除。
    /// </summary>
    public void UpsertEntry(
        string author, string repoName, string source,
        string? id, string? reference, string? reference2, string? description,
        string? filesJson, string? installType, string? pipJson,
        string? preemptionsJson, string? nodenamePattern, string? category,
        string? tagsJson, string? jsPath)
    {
        using var conn = _factory.Open();
        // v1.0.0.x (2026-09-16) T43i.4+user「无变动不写入数据库」:
        // 先 SELECT 11 个 raw JSON 列(id/reference/reference2/description/files_json/
        // install_type/pip_json/preemptions_json/nodename_pattern/category/tags_json/
        // js_path)对比 caller 给的值 ——
        // - 行不存在 → INSERT 新行(填 first_seen_at + last_ingested_at + 11 列)
        // - 行存在 + 11 列内容完全相同 → 跳过(什么都不写,last_ingested_at 也不动)
        // - 行存在 + 11 列任一不同 → UPDATE 11 列 + last_ingested_at(保持 first_seen_at 不变)
        //
        // 实现:用 COALESCE 把 NULL 转空字符串方便 string.Equals,但 pip_json 等 JSON 字段
        // 不能用空串 → 改成 NullOrEqualsIsDbNull 手动 null-safe equals。Semantic 简单:
        // 两个都 null / 两个 string 相等 → same;任一不同 → diff。
        //
        // 性能:每个 entry 一次 SELECT + 一次可能的 UPDATE。新增 entry:2 round trip(SELECT 后 INSERT)
        // 改 entry:2 round trip。无变化 entry:1 round trip(SELECT)。5942 entry 全扫描 ≈ 6000 SELECT。
        // SQLite WAL + 索引 (author, repo_name) PK 查询 < 0.1ms/次,总开销可接受。
        var existing = SelectEntryRawJsonColumns(conn, author, repoName);
        bool isNew = existing is null;
        var incoming = new EntryRawJsonColumns(
            id, reference, reference2, description, filesJson, installType, pipJson,
            preemptionsJson, nodenamePattern, category, tagsJson, jsPath);
        bool hasChanges = isNew || !EntryRawJsonColumnsEqual(existing!, incoming);

        using var cmd = conn.CreateCommand();
        if (isNew)
        {
            // 新 entry:INSERT 全部列
            cmd.CommandText = @"
                INSERT INTO nodelist_entries
                    (author, repo_name, first_seen_at, last_ingested_at, source,
                     id, reference, reference2, description, files_json, install_type,
                     pip_json, preemptions_json, nodename_pattern, category,
                     tags_json, js_path)
                VALUES
                    (@author, @repo, @now, @now, @source,
                     @id, @reference, @reference2, @description, @filesJson, @installType,
                     @pipJson, @preemptionsJson, @nodenamePattern, @category,
                     @tagsJson, @jsPath)";
            cmd.Parameters.AddWithValue("@author", author);
            cmd.Parameters.AddWithValue("@repo", repoName);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reference", (object?)reference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reference2", (object?)reference2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@filesJson", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@installType", (object?)installType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pipJson", (object?)pipJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@preemptionsJson", (object?)preemptionsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@nodenamePattern", (object?)nodenamePattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tagsJson", (object?)tagsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@jsPath", (object?)jsPath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        else if (hasChanges)
        {
            // 内容变化:UPDATE 11 列 + last_ingested_at(first_seen_at 不改)
            cmd.CommandText = @"
                UPDATE nodelist_entries SET
                    last_ingested_at = @now,
                    id = @id,
                    reference = @reference,
                    reference2 = @reference2,
                    description = @description,
                    files_json = @filesJson,
                    install_type = @installType,
                    pip_json = @pipJson,
                    preemptions_json = @preemptionsJson,
                    nodename_pattern = @nodenamePattern,
                    category = @category,
                    tags_json = @tagsJson,
                    js_path = @jsPath
                WHERE author = @author AND repo_name = @repo";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reference", (object?)reference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reference2", (object?)reference2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@filesJson", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@installType", (object?)installType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pipJson", (object?)pipJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@preemptionsJson", (object?)preemptionsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@nodenamePattern", (object?)nodenamePattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tagsJson", (object?)tagsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@jsPath", (object?)jsPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@author", author);
            cmd.Parameters.AddWithValue("@repo", repoName);
            cmd.ExecuteNonQuery();
        }
        // else: 内容无变化 → 不发 SQL,啥都不写
    }

    /// <summary>v1.0.0.x T43i.4+user:11 个 raw JSON 列变更对比载体。
    /// 跟 NodelistRepository 同文件命名空间,private — 仅 UpsertEntry 内部用。</summary>
    private sealed record EntryRawJsonColumns(
        string? Id, string? Reference, string? Reference2, string? Description,
        string? FilesJson, string? InstallType, string? PipJson,
        string? PreemptionsJson, string? NodenamePattern, string? Category,
        string? TagsJson, string? JsPath);

    /// <summary>v1.0.0.x T43i.4+user:SELECT 现有 entry 的 11 个 raw JSON 列 ——
    /// 行不存在返 null,存在返 EntryRawJsonColumns(SQL NULL → C# null)。</summary>
    private static EntryRawJsonColumns? SelectEntryRawJsonColumns(
        SqliteConnection conn, string author, string repoName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, reference, reference2, description, files_json, install_type,
                   pip_json, preemptions_json, nodename_pattern, category, tags_json, js_path
            FROM nodelist_entries
            WHERE author = @a AND repo_name = @r";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        string? Get(int i) => rdr.IsDBNull(i) ? null : rdr.GetString(i);
        return new EntryRawJsonColumns(
            Get(0), Get(1), Get(2), Get(3), Get(4), Get(5),
            Get(6), Get(7), Get(8), Get(9), Get(10), Get(11));
    }

    /// <summary>v1.0.0.x T43i.4+user:11 个 raw JSON 列内容对比 ——
    /// 两边都 null / 两边 string 相等 → same;任一不同 → diff。
    /// 用 string.Equals(Ordinal) 而非 ReferenceEquals。</summary>
    private static bool EntryRawJsonColumnsEqual(EntryRawJsonColumns a, EntryRawJsonColumns b)
    {
        return StringEq(a.Id, b.Id)
            && StringEq(a.Reference, b.Reference)
            && StringEq(a.Reference2, b.Reference2)
            && StringEq(a.Description, b.Description)
            && StringEq(a.FilesJson, b.FilesJson)
            && StringEq(a.InstallType, b.InstallType)
            && StringEq(a.PipJson, b.PipJson)
            && StringEq(a.PreemptionsJson, b.PreemptionsJson)
            && StringEq(a.NodenamePattern, b.NodenamePattern)
            && StringEq(a.Category, b.Category)
            && StringEq(a.TagsJson, b.TagsJson)
            && StringEq(a.JsPath, b.JsPath);
    }

    private static bool StringEq(string? x, string? y)
        => x is null ? y is null : y is not null && x.Equals(y, StringComparison.Ordinal);

    public void UpsertDetail(Detail d)
    {
        using var conn = _factory.Open();
        // v1.0.0.x (2026-09-16) T43i.4+user「无变动不写入数据库」+ user「增加一个 release
        // 就更改」:raw_json 是云端 API 完整响应,branches/recentReleases/releaseCount/
        // latestRelease 都嵌在里面。新 release / 新 branch = raw_json 内容变化 → UPDATE
        // 整行(13 数据列 + fetched_at)。raw_json 完全相同 → 跳过,啥都不写。
        //
        // 实现:先 SELECT 现有 raw_json,跟 d.RawJson StringEquals(Ordinal) 对比。
        // - 行不存在 → INSERT 全部 18 列
        // - 行存在 + raw_json 相同 → 跳过
        // - 行存在 + raw_json 不同 → UPDATE 整行
        var existingRaw = SelectDetailRawJson(conn, d.Author, d.RepoName, d.Version);
        bool isNew = existingRaw is null;
        bool hasChanges = isNew || !StringEq(existingRaw, d.RawJson);

        using var cmd = conn.CreateCommand();
        if (isNew)
        {
            cmd.CommandText = @"
                INSERT INTO nodelist_details
                    (author, repo_name, version, description, stars, watchers, forks, license,
                     default_branch, updated_at, pushed_at, html_url, language, open_issues,
                     topics, raw_json, host, fetched_at)
                VALUES (@author, @repo, @ver, @desc, @stars, @watchers, @forks, @lic,
                        @branch, @updated, @pushed, @url, @lang, @issues,
                        @topics, @raw, @host, @now)";
            AddDetailParameters(cmd, d);
            cmd.ExecuteNonQuery();
        }
        else if (hasChanges)
        {
            cmd.CommandText = @"
                UPDATE nodelist_details SET
                    description = @desc,
                    stars = @stars,
                    watchers = @watchers,
                    forks = @forks,
                    license = @lic,
                    default_branch = @branch,
                    updated_at = @updated,
                    pushed_at = @pushed,
                    html_url = @url,
                    language = @lang,
                    open_issues = @issues,
                    topics = @topics,
                    raw_json = @raw,
                    host = @host,
                    fetched_at = @now
                WHERE author = @author AND repo_name = @repo AND version = @ver";
            AddDetailParameters(cmd, d);
            cmd.ExecuteNonQuery();
        }
        // else: raw_json 完全相同 → 不发 SQL,啥都不写(用户原话「无变动不写入」)
    }

    /// <summary>v1.0.0.x T43i.4+user:把 Detail 18 个字段绑成 NodelistRepository.Detail 的 SqliteCommand parameters。
    /// INSERT 和 UPDATE 共用 — 字段语义对等。</summary>
    private static void AddDetailParameters(SqliteCommand cmd, Detail d)
    {
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
    }

    /// <summary>v1.0.0.x T43i.4+user:SELECT 现有 detail 的 raw_json ——
    /// 行不存在返 null,存在返 raw_json string(SQL NULL → C# null)。</summary>
    private static string? SelectDetailRawJson(SqliteConnection conn, string author, string repoName, string version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT raw_json FROM nodelist_details
            WHERE author = @a AND repo_name = @r AND version = @v";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        cmd.Parameters.AddWithValue("@v", version);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    public List<Entry> GetAllEntries()
    {
        var list = new List<Entry>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        // v1.0.0.x T43h+user:SELECT 23 列(5 旧 + 18 新 raw JSON 字段)。
        // v1.0.0.x T43i.1+user:加 description → 24 列(5 旧 + 19 新 raw JSON 字段)。
        // v1.0.0.x T43i.2.2-fix:删 5 列(apt_dependency/dependencies_json/nickname/
        // last_update/badges_json 各 ≤0.017% 覆盖率)→ 19 列。
        // 老 DB backfill 后新列全 NULL,Read 通过 IsDBNull 兜底返回 null。
        cmd.CommandText = @"SELECT author, repo_name, first_seen_at, last_ingested_at, source,
                id, reference, reference2, description, files_json, install_type,
                pip_json, preemptions_json, nodename_pattern, category, tags_json, js_path
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
                reader.IsDBNull(8) ? null : reader.GetString(8),         // description (T43i.1)
                reader.IsDBNull(9) ? null : reader.GetString(9),         // files_json
                reader.IsDBNull(10) ? null : reader.GetString(10),       // install_type
                reader.IsDBNull(11) ? null : reader.GetString(11),       // pip_json
                reader.IsDBNull(12) ? null : reader.GetString(12),       // preemptions_json
                reader.IsDBNull(13) ? null : reader.GetString(13),       // nodename_pattern
                reader.IsDBNull(14) ? null : reader.GetString(14),       // category
                reader.IsDBNull(15) ? null : reader.GetString(15),       // tags_json
                reader.IsDBNull(16) ? null : reader.GetString(16)));     // js_path
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
