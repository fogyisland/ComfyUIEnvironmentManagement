using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class NodeVersionRepositoryTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeVersionRepository _repo;

    public NodeVersionRepositoryTests()
    {
        _db = new TestDb();
        _repo = new NodeVersionRepository(new CatalogCacheStore(_db.Path));
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void UpsertBatch_ThenListByNode_ReturnsInsertedInDescendingOrder()
    {
        var items = new (string, VersionInfo)[]
        {
            ("node-1", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2025-01-01T00:00:00Z", IsPrerelease = false }),
            ("node-1", new VersionInfo { Tag = "v1.1.0", PublishedAt = "2025-06-01T00:00:00Z", IsPrerelease = false }),
            ("node-1", new VersionInfo { Tag = "v1.2.0", PublishedAt = "2025-12-01T00:00:00Z", IsPrerelease = false }),
            ("node-2", new VersionInfo { Tag = "v0.1.0", PublishedAt = "2024-01-01T00:00:00Z", IsPrerelease = false }),
        };

        var n = _repo.UpsertBatch(items);
        Assert.Equal(4, n);

        var list1 = _repo.ListByNode("node-1");
        Assert.Equal(3, list1.Count);
        Assert.Equal("v1.2.0", list1[0].Tag);  // 最新在前
        Assert.Equal("v1.1.0", list1[1].Tag);
        Assert.Equal("v1.0.0", list1[2].Tag);

        var list2 = _repo.ListByNode("node-2");
        Assert.Single(list2);
        Assert.Equal("v0.1.0", list2[0].Tag);
    }

    [Fact]
    public void UpsertBatch_SameNodeDifferentVersions_AllPersist()
    {
        var items = Enumerable.Range(1, 12).Select(i => (
            "node-1",
            new VersionInfo
            {
                Tag = $"v0.{i}.0",
                PublishedAt = $"2025-{i:D2}-01T00:00:00Z",
                IsPrerelease = false,
            }
        )).ToArray();

        _repo.UpsertBatch(items);

        var list = _repo.ListByNode("node-1");
        Assert.Equal(12, list.Count);
        // 最新在前
        Assert.Equal("v0.12.0", list[0].Tag);
    }

    [Fact]
    public void UpsertBatch_ReplacesExistingForSameNode()
    {
        // 第一次写
        _repo.UpsertBatch(new[]
        {
            ("n", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2024-01-01T00:00:00Z" }),
            ("n", new VersionInfo { Tag = "v2.0.0", PublishedAt = "2025-01-01T00:00:00Z" }),
        });
        // 第二次写(覆盖) — 只剩新的
        _repo.UpsertBatch(new[]
        {
            ("n", new VersionInfo { Tag = "v3.0.0", PublishedAt = "2026-01-01T00:00:00Z" }),
        });

        var list = _repo.ListByNode("n");
        Assert.Single(list);
        Assert.Equal("v3.0.0", list[0].Tag);
    }

    [Fact]
    public void ListByNode_NoVersions_ReturnsEmpty()
    {
        var list = _repo.ListByNode("nonexistent");
        Assert.Empty(list);
    }

    [Fact]
    public void UpsertBatch_PreservesPrereleaseFlag()
    {
        _repo.UpsertBatch(new[]
        {
            ("n", new VersionInfo { Tag = "v2.0.0-rc1", PublishedAt = "2025-06-01T00:00:00Z", IsPrerelease = true }),
        });
        var list = _repo.ListByNode("n");
        Assert.Single(list);
        Assert.True(list[0].IsPrerelease);
        Assert.Contains("预发布", list[0].DisplayLabel);
    }
}
