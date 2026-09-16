// v1.0.0.x (2026-09-16) T43i.4+user「无变动不写入数据库」+ user「增加一个 release
// 就更改」:nodelist_details 增量 upsert 行为测试。
//
// raw_json 是云端 API 完整响应,branches/recentReleases/releaseCount/latestRelease
// 都在里面。新 release / 新 branch = raw_json 内容变化 → UPDATE 整行。raw_json 完全
// 相同 → 跳过(啥都不写,包括 fetched_at 也不动)。

using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class NodelistDetailIncrementalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly NodelistRepository _repo;

    public NodelistDetailIncrementalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nodelist-detail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "state.db"));
        _repo = new NodelistRepository(_factory);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private static NodelistRepository.Detail MakeDetail(string rawJson) =>
        new("ltdrdata", "ComfyUI-Manager", "main",
            Description: "test repo",
            Stars: 100, Watchers: 5, Forks: 10,
            License: "MIT", DefaultBranch: "main",
            UpdatedAt: "2025-01-01T00:00:00Z", PushedAt: "2025-01-02T00:00:00Z",
            HtmlUrl: "https://github.com/ltdrdata/ComfyUI-Manager",
            Language: "Python", OpenIssues: 3,
            Topics: "ai,comfy",
            RawJson: rawJson,
            Host: "https://github.pudafo.com",
            FetchedAt: DateTime.UtcNow.ToString("o"));

    private void EnsureEntry()
    {
        // nodelist_details FK 引 nodelist_entries(author, repo_name) → 必须先 upsert entry。
        // 测试用 11 列全 null 的 entry(只填 PK + source 字段)。
        _repo.UpsertEntry(
            "ltdrdata", "ComfyUI-Manager", "json",
            id: null, reference: null, reference2: null, description: null,
            filesJson: null, installType: null, pipJson: null,
            preemptionsJson: null, nodenamePattern: null, category: null,
            tagsJson: null, jsPath: null);
    }

    [Fact]
    public void UpsertDetail_NoChange_DoesNotUpdateFetchedAt()
    {
        EnsureEntry();
        // raw_json 包含 releaseCount=3
        var rawV1 = """{"repository":{"releaseCount":3,"recentReleases":[{"tag_name":"1.0.0"}]}}""";
        var firstFetched = DateTime.UtcNow.AddSeconds(-10).ToString("o");
        _repo.UpsertDetail(MakeDetail(rawV1) with { FetchedAt = firstFetched });

        var read1 = ReadDetailFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");

        System.Threading.Thread.Sleep(50);

        // 第二次入库 — raw_json 完全相同
        var newFetched = DateTime.UtcNow.ToString("o");
        _repo.UpsertDetail(MakeDetail(rawV1) with { FetchedAt = newFetched });

        var read2 = ReadDetailFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");

        // fetched_at 必须保持原值(用户原话「无变动不写入」严格对齐)
        Assert.Equal(read1, read2);
        // 跟 firstFetched 字符串一致(没被 UPDATE 覆盖)
        Assert.Equal(firstFetched, read2);
    }

    [Fact]
    public void UpsertDetail_RawJsonChange_UpdatesAllColumnsAndFetchedAt()
    {
        EnsureEntry();
        // 第一次入库 — releaseCount=3
        var rawV1 = """{"repository":{"releaseCount":3,"recentReleases":[{"tag_name":"1.0.0"}]}}""";
        var firstFetched = DateTime.UtcNow.AddSeconds(-10).ToString("o");
        _repo.UpsertDetail(MakeDetail(rawV1) with { FetchedAt = firstFetched });

        System.Threading.Thread.Sleep(50);

        // 第二次入库 — raw_json 变了(releaseCount=4,新加一个 release)
        var rawV2 = """
            {"repository":{"releaseCount":4,"recentReleases":[
                {"tag_name":"1.1.0"},
                {"tag_name":"1.0.0"}
            ]}}
            """;
        var newFetched = DateTime.UtcNow.ToString("o");
        _repo.UpsertDetail(MakeDetail(rawV2) with { FetchedAt = newFetched });

        var read2 = ReadDetailRawJsonAndFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");

        // fetched_at 必须更新(因为 raw_json 变了)
        Assert.Equal(newFetched, read2.fetchedAt);
        Assert.NotEqual(firstFetched, read2.fetchedAt);
        // raw_json 真的换了
        Assert.Equal(rawV2, read2.rawJson);
    }

    [Fact]
    public void UpsertDetail_NewEntry_Inserts()
    {
        EnsureEntry();
        var rawJson = """{"repository":{"releaseCount":1}}""";
        _repo.UpsertDetail(MakeDetail(rawJson));

        var read = ReadDetailRawJsonAndFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");
        Assert.Equal(rawJson, read.rawJson);
        Assert.NotNull(read.fetchedAt);
    }

    [Fact]
    public void UpsertDetail_OnlyStarsChange_StillSkips()
    {
        EnsureEntry();
        // raw_json 不变,即使 Stars 字段传了不同值 → 仍跳过
        // (因为对比只比 raw_json,Stars 是 raw_json 派生)
        var rawJson = """{"repository":{"releaseCount":3}}""";
        _repo.UpsertDetail(MakeDetail(rawJson) with { Stars = 100 });

        System.Threading.Thread.Sleep(50);

        // stars 传了不同值,但 raw_json 一样
        var read1 = ReadDetailFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");
        _repo.UpsertDetail(MakeDetail(rawJson) with { Stars = 999 });
        var read2 = ReadDetailFetchedAt(_factory, "ltdrdata", "ComfyUI-Manager", "main");

        // fetched_at 不变(因为 raw_json 一样 → 跳过)
        Assert.Equal(read1, read2);
    }

    private static string? ReadDetailFetchedAt(
        SqliteConnectionFactory factory, string author, string repoName, string version)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT fetched_at FROM nodelist_details
            WHERE author = @a AND repo_name = @r AND version = @v";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        cmd.Parameters.AddWithValue("@v", version);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : (string)r;
    }

    private static (string? rawJson, string? fetchedAt) ReadDetailRawJsonAndFetchedAt(
        SqliteConnectionFactory factory, string author, string repoName, string version)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT raw_json, fetched_at FROM nodelist_details
            WHERE author = @a AND repo_name = @r AND version = @v";
        cmd.Parameters.AddWithValue("@a", author);
        cmd.Parameters.AddWithValue("@r", repoName);
        cmd.Parameters.AddWithValue("@v", version);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return (null, null);
        return (
            rdr.IsDBNull(0) ? null : rdr.GetString(0),
            rdr.IsDBNull(1) ? null : rdr.GetString(1));
    }
}