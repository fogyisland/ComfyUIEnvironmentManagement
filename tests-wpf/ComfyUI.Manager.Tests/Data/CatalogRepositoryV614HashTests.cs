using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogRepositoryV614HashTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CatalogCacheStore _store;
    private readonly CatalogRepository _repo;

    public CatalogRepositoryV614HashTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"comfy-repo-v614-{Guid.NewGuid():N}.db");
        _store = new CatalogCacheStore(_dbPath);
        _repo = new CatalogRepository(_store);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    private static CatalogEntry MakeEntry(string id, string pkg, string author = "alice")
    {
        var entry = new CatalogEntry
        {
            Id = id,
            SourceUrl = "https://example.com/catalog.json",
            Package = pkg,
            RawMetadata = new Dictionary<string, object?>
            {
                ["id"] = pkg,
                ["author"] = author,
                ["title"] = $"Title of {pkg}",
                ["description"] = $"Desc of {pkg}",
            },
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        };
        entry.HtmlUrl = $"https://github.com/{author}/{pkg}";
        entry.Homepage = $"https://example.com/{pkg}";
        entry.Language = "Python";
        entry.ForksCount = 10;
        entry.OpenIssuesCount = 5;
        entry.ReleaseTag = "v1.0.0";
        entry.SubscribersCount = 100;
        entry.CreatedAt = "2025-01-01T00:00:00Z";
        return entry;
    }

    [Fact]
    public void UpsertBatch_ComputesAndPersistsContentHash()
    {
        var entries = new[] { MakeEntry("e1", "pkg-x"), MakeEntry("e2", "pkg-y") };
        _repo.UpsertBatch(entries);

        var hashes = GetHashes(_dbPath, "pkg-x", "pkg-y");

        Assert.Equal(CatalogEntryHasher.ComputeHash(MakeEntry("e1", "pkg-x")), hashes["pkg-x"]);
        Assert.Equal(CatalogEntryHasher.ComputeHash(MakeEntry("e2", "pkg-y")), hashes["pkg-y"]);
    }

    [Fact]
    public void UpsertBatch_SameContent_SameHash_Idempotent()
    {
        _repo.UpsertBatch(new[] { MakeEntry("e1", "pkg-x") });
        var firstHash = GetHashes(_dbPath, "pkg-x")["pkg-x"];

        _repo.UpsertBatch(new[] { MakeEntry("e1", "pkg-x") });
        var secondHash = GetHashes(_dbPath, "pkg-x")["pkg-x"];

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public async Task GetContentHashesBySourceAsync_ReturnsDict()
    {
        _repo.UpsertBatch(new[] {
            MakeEntry("e1", "pkg-a"),
            MakeEntry("e2", "pkg-b"),
            MakeEntry("e3", "pkg-c"),
        });

        var hashes = await _repo.GetContentHashesBySourceAsync(
            "https://example.com/catalog.json");

        Assert.Equal(3, hashes.Count);
        Assert.Contains("pkg-a", hashes.Keys);
        Assert.Contains("pkg-b", hashes.Keys);
        Assert.Contains("pkg-c", hashes.Keys);
    }

    [Fact]
    public void Roundtrip_8NewColumns_PreservedThroughRead()
    {
        var entry = MakeEntry("e1", "pkg-x");
        _repo.Upsert(entry);

        var fetched = _repo.Search("", 10).First(e => e.Package == "pkg-x");

        Assert.Equal("https://github.com/alice/pkg-x", fetched.HtmlUrl);
        Assert.Equal("https://example.com/pkg-x", fetched.Homepage);
        Assert.Equal("Python", fetched.Language);
        Assert.Equal(10, fetched.ForksCount);
        Assert.Equal(5, fetched.OpenIssuesCount);
        Assert.Equal("v1.0.0", fetched.ReleaseTag);
        Assert.Equal(100, fetched.SubscribersCount);
        Assert.Equal("2025-01-01T00:00:00Z", fetched.CreatedAt);
    }

    /// <summary>
    /// v0.6.14 hotfix: UpdateLatestVersions 必须按 (source_url, package) 而不是 id 写,
    /// 否则增量 refresh 时 CatalogFetcher 给 Updated entry 分配的新 GUID 永远匹配不上
    /// DB 里现有 row 的老 GUID → latest_version 静默不更新。
    /// 预填老 GUID 的 row,然后用 (source_url, package, "v2.0.0") 调 → 验证行被更新。
    /// </summary>
    [Fact]
    public void UpdateLatestVersions_KeysBySourceUrlAndPackage_NotById()
    {
        // Arrange: 预填一行 id="old-guid-A", package="pkg-x", source_url=...
        var sourceUrl = "https://example.com/catalog.json";
        var pre = MakeEntry("old-guid-A", "pkg-x");
        pre.SourceUrl = sourceUrl;
        _repo.UpsertBatch(new[] { pre });

        // Act: 用 (source_url, package, version) — 不传 id。模拟"refresh 拉了新 GUID
        // 但 service 只拿得到 (source_url, package, tag)"的真实场景。
        var n = _repo.UpdateLatestVersions(new[] {
            (sourceUrl, "pkg-x", "v2.0.0"),
        });

        // Assert: 1 行被更新,latest_version 写到了 pkg-x 的现有 row
        Assert.Equal(1, n);
        var fetched = _repo.Search("pkg-x", 10).Single();
        Assert.Equal("v2.0.0", fetched.LatestVersion);
        // id 没变(老 GUID 仍在 row 上)
        Assert.Equal("old-guid-A", fetched.Id);
    }

    /// <summary>
    /// v0.6.14 hotfix: UpdateLatestVersions 一次批量里多个不同 (source_url, package)
    /// 必须各自打到对的行,不能跨行写串。
    /// </summary>
    [Fact]
    public void UpdateLatestVersions_MultipleEntries_EachHitsItsOwnRow()
    {
        var url = "https://example.com/catalog.json";  // MakeEntry 默认 URL
        _repo.UpsertBatch(new[] {
            MakeEntry("old-A", "pkg-a"),
            MakeEntry("old-B", "pkg-b"),
        });

        var n = _repo.UpdateLatestVersions(new[] {
            (url, "pkg-a", "v1.0.0"),
            (url, "pkg-b", "v9.9.9"),
        });

        Assert.Equal(2, n);
        Assert.Equal("v1.0.0",
            _repo.Search("pkg-a", 10).Single().LatestVersion);
        Assert.Equal("v9.9.9",
            _repo.Search("pkg-b", 10).Single().LatestVersion);
    }

    /// <summary>
    /// v0.6.14 hotfix: 不同 source_url + 同一 package 的两条 row(罕见但合法)
    /// 各自按 source_url 区分;不能写串。
    /// </summary>
    [Fact]
    public void UpdateLatestVersions_DifferentSources_SamePackage_EachHitsOwnRow()
    {
        // MakeEntry 写死 url,所以手动造两条 entry
        var a = MakeEntry("old-A", "pkg-x");
        a.SourceUrl = "https://a.example.com/c.json";
        var b = MakeEntry("old-B", "pkg-x");
        b.SourceUrl = "https://b.example.com/c.json";
        _repo.UpsertBatch(new[] { a, b });

        var n = _repo.UpdateLatestVersions(new[] {
            ("https://a.example.com/c.json", "pkg-x", "v1.0.0"),
            ("https://b.example.com/c.json", "pkg-x", "v2.0.0"),
        });

        Assert.Equal(2, n);
        Assert.Equal("v1.0.0",
            _repo.Search("pkg-x", 10).Single(e => e.SourceUrl == "https://a.example.com/c.json").LatestVersion);
        Assert.Equal("v2.0.0",
            _repo.Search("pkg-x", 10).Single(e => e.SourceUrl == "https://b.example.com/c.json").LatestVersion);
    }

    /// <summary>
    /// v0.6.14 hotfix: source_url+package 都不存在的 tuple 应该被静默跳过
    /// (0 行被 UPDATE),不应该 NRE 也不应该 UPDATE 到不存在的行。
    /// </summary>
    [Fact]
    public void UpdateLatestVersions_NonExistentPackage_SilentlySkipped()
    {
        _repo.UpsertBatch(new[] { MakeEntry("e1", "pkg-exists") });

        var n = _repo.UpdateLatestVersions(new[] {
            ("https://example.com/catalog.json", "pkg-not-there", "v1.0.0"),
        });

        Assert.Equal(0, n);
        // 现有 row 没被污染
        Assert.Null(_repo.Search("pkg-exists", 10).Single().LatestVersion);
    }

    /// <summary>
    /// v0.6.14 hotfix: 空字符串 version 被跳过(沿用既有"items 中 null/空 version 跳过"约定)。
    /// </summary>
    [Fact]
    public void UpdateLatestVersions_EmptyVersion_Skipped()
    {
        var sourceUrl = "https://example.com/catalog.json";
        var pre = MakeEntry("e1", "pkg-x");
        pre.SourceUrl = sourceUrl;
        _repo.UpsertBatch(new[] { pre });

        var n = _repo.UpdateLatestVersions(new[] {
            (sourceUrl, "pkg-x", ""),
        });

        Assert.Equal(0, n);
        Assert.Null(_repo.Search("pkg-x", 10).Single().LatestVersion);
    }

    private static Dictionary<string, string> GetHashes(string dbPath, params string[] packages)
    {
        var result = new Dictionary<string, string>();
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT package, content_hash FROM catalog_cache " +
                          $"WHERE package IN ({string.Join(",", packages.Select((_, i) => $"@p{i}"))})";
        for (int i = 0; i < packages.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", packages[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
}