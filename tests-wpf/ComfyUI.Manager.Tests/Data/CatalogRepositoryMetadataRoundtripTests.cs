using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>v0.6.13-B: CatalogRepository 对 11 个 metadata 列的写入/读取 roundtrip。</summary>
public class CatalogRepositoryMetadataRoundtripTests : IDisposable
{
    private readonly CatalogCacheStore _store;
    private readonly CatalogRepository _repo;
    private readonly string _dbPath;

    public CatalogRepositoryMetadataRoundtripTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"comfy-repo-meta-{Guid.NewGuid():N}.db");
        _store = new CatalogCacheStore(_dbPath);
        _repo = new CatalogRepository(_store);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var wal = _dbPath + "-wal"; var shm = _dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { /* best-effort temp cleanup */ }
    }

    private static CatalogEntry MakeEntry(string id, string pkg) => new()
    {
        Id = id,
        SourceUrl = "https://example.com/catalog.json",
        Package = pkg,
        RawMetadata = new Dictionary<string, object?>(),
        CachedAt = "2026-08-12T00:00:00",
        ExpiresAt = "2026-08-13T00:00:00",
    };

    [Fact]
    public void Upsert_PopulatesAllMetadataFields()
    {
        var entry = MakeEntry("e1", "pkg-1");
        entry.License = "MIT";
        entry.Tags = new[] { "img2img", "controlnet" };
        entry.Stars = 1234;
        entry.Downloads = 56789;
        entry.LastCommit = "2026-08-10T12:34:56Z";
        entry.ReadmeMarkdown = "# Hello\n\nWorld";
        entry.LatestChangelog = "## v1.2.3\n- fix bug";
        entry.Deprecated = true;
        entry.PythonCompat = new[] { "3.10", "3.11" };
        entry.OsCompat = new[] { "windows", "linux", "macos" };
        entry.MetadataFetchedAt = "2026-08-12T03:00:00Z";
        _repo.Upsert(entry);

        var read = _repo.Search("", 10).Single(e => e.Id == "e1");
        Assert.Equal("MIT", read.License);
        Assert.Equal(new[] { "img2img", "controlnet" }, read.Tags);
        Assert.Equal(1234, read.Stars);
        Assert.Equal(56789, read.Downloads);
        Assert.Equal("2026-08-10T12:34:56Z", read.LastCommit);
        Assert.Equal("# Hello\n\nWorld", read.ReadmeMarkdown);
        Assert.Equal("## v1.2.3\n- fix bug", read.LatestChangelog);
        Assert.True(read.Deprecated);
        Assert.Equal(new[] { "3.10", "3.11" }, read.PythonCompat);
        Assert.Equal(new[] { "windows", "linux", "macos" }, read.OsCompat);
        Assert.Equal("2026-08-12T03:00:00Z", read.MetadataFetchedAt);
    }

    [Fact]
    public void Upsert_NullFields_DefaultToNullOrEmpty()
    {
        _repo.Upsert(MakeEntry("e2", "pkg-2"));
        var read = _repo.Search("", 10).Single(e => e.Id == "e2");
        Assert.Null(read.License);
        Assert.Empty(read.Tags);
        Assert.Equal(0, read.Stars);
        Assert.Equal(0, read.Downloads);
        Assert.Null(read.LastCommit);
        Assert.Null(read.ReadmeMarkdown);
        Assert.Null(read.LatestChangelog);
        Assert.False(read.Deprecated);
        Assert.Empty(read.PythonCompat);
        Assert.Empty(read.OsCompat);
        Assert.Null(read.MetadataFetchedAt);
    }

    [Fact]
    public void UpsertBatch_HandlesMultipleEntries()
    {
        var a = MakeEntry("a", "PkgA"); a.Stars = 100;
        var b = MakeEntry("b", "PkgB"); b.Stars = 200;
        var count = _repo.UpsertBatch(new[] { a, b });
        Assert.Equal(2, count);
        var rows = _repo.Search("", 10);
        Assert.Equal(100, rows.Single(r => r.Id == "a").Stars);
        Assert.Equal(200, rows.Single(r => r.Id == "b").Stars);
    }
}