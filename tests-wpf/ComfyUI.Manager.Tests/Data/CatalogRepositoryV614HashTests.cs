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