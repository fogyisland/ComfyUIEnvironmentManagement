using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogRepositoryTypedFieldsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly CatalogRepository _repo;

    public CatalogRepositoryTypedFieldsTests()
    {
        _db = new TestDb();
        _repo = new CatalogRepository(new CatalogCacheStore(_db.Path));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void UpsertBatch_PopulatesAuthor_FromRawMetadata()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-author",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "alice" },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Single(list);
        Assert.Equal("alice", list[0].Author);
    }

    [Fact]
    public void UpsertBatch_PopulatesInstallType_And_Reference()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-types",
            RawMetadata = new Dictionary<string, object?>
            {
                ["install_type"] = "git-clone",
                ["reference"] = "https://github.com/foo/bar",
            },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Equal("git-clone", list[0].InstallType);
        Assert.Equal("https://github.com/foo/bar", list[0].Reference);
    }

    [Fact]
    public void UpsertBatch_ParsesPipList_IntoPipRequirements()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-pip",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "numpy>=1.24.0", "huggingface-hub" },
            },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Equal(2, list[0].PipRequirements.Count);
        Assert.Equal("numpy", list[0].PipRequirements[0].Name);
        Assert.Equal(">=1.24.0", list[0].PipRequirements[0].Specifier);
        Assert.Equal("huggingface-hub", list[0].PipRequirements[1].Name);
        Assert.Null(list[0].PipRequirements[1].Specifier);
    }

    [Fact]
    public void UpsertBatch_OnConflict_UpdatesTypedColumns()
    {
        var entry1 = new CatalogEntry
        {
            Id = "fixed-id",
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-update",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "alice" },
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry1 });
        var entry2 = new CatalogEntry
        {
            Id = "fixed-id",
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-update",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "bob" },
            CachedAt = "2026-08-07T00:00:01Z",
            ExpiresAt = "2026-08-08T00:00:01Z",
        };
        _repo.UpsertBatch(new[] { entry2 });

        var list = _repo.Search("", 0);
        Assert.Single(list);
        Assert.Equal("bob", list[0].Author);
    }

    [Fact]
    public void UpsertBatch_JsonElementPipArray_RoundTripsViaSqlite()
    {
        // 模拟 SQLite 重读后 raw_metadata["pip"] 是 JsonElement (而非 List<object?>)
        var rawJson = "{\"pip\":[\"numpy>=1.24.0\",\"huggingface-hub\"],\"author\":\"alice\"}";
        var rawMeta = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new Dictionary<string, object?>();
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-jsonel",
            RawMetadata = rawMeta,
            CachedAt = "2026-08-08T00:00:00Z",
            ExpiresAt = "2026-08-09T00:00:00Z",
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.Search("", 0);
        Assert.Single(list);
        Assert.Equal("alice", list[0].Author);
        // 关键断言 — JsonElement 数组没被识别时这里会是 0
        Assert.Equal(2, list[0].PipRequirements.Count);
        Assert.Equal("numpy", list[0].PipRequirements[0].Name);
        Assert.Equal(">=1.24.0", list[0].PipRequirements[0].Specifier);
        Assert.Equal("huggingface-hub", list[0].PipRequirements[1].Name);
        Assert.Null(list[0].PipRequirements[1].Specifier);
    }

    [Fact]
    public void ListNonExpired_AfterMigration_ReturnsTypedFields()
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-listnon",
            RawMetadata = new Dictionary<string, object?> { ["author"] = "alice" },
            CachedAt = "2026-08-08T00:00:00Z",
            ExpiresAt = "2099-01-01T00:00:00Z",  // future, not expired
        };
        _repo.UpsertBatch(new[] { entry });

        var list = _repo.ListNonExpired(DateTime.UtcNow);
        Assert.Single(list);
        Assert.Equal("alice", list[0].Author);
        Assert.Empty(list[0].PipRequirements);  // no pip field
    }
}
