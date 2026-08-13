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
    private readonly CatalogRepository _catRepo;
    private const string SourceUrl = "https://example.com/catalog.json";

    public NodeVersionRepositoryTests()
    {
        _db = new TestDb();
        var store = new CatalogCacheStore(_db.Path);
        _repo = new NodeVersionRepository(store);
        _catRepo = new CatalogRepository(store);
    }
    public void Dispose() => _db.Dispose();

    /// <summary>
    /// v0.6.14: UpsertBatch 现在按 (source_url, package) 寻址,所以 node_id="node-1"
    /// 必须先在 catalog_cache 里有对应 row 才能写 node_versions。
    /// </summary>
    private void SeedCatalog(string nodeId, string package)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = nodeId,
            SourceUrl = SourceUrl,
            Package = package,
            CachedAt = "2026-08-13T00:00:00Z",
            ExpiresAt = "2026-08-14T00:00:00Z",
        });
    }

    [Fact]
    public void UpsertBatch_ThenListByNode_ReturnsInsertedInDescendingOrder()
    {
        SeedCatalog("node-1", "pkg-1");
        SeedCatalog("node-2", "pkg-2");
        var items = new (string, string, VersionInfo)[]
        {
            (SourceUrl, "pkg-1", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2025-01-01T00:00:00Z", IsPrerelease = false }),
            (SourceUrl, "pkg-1", new VersionInfo { Tag = "v1.1.0", PublishedAt = "2025-06-01T00:00:00Z", IsPrerelease = false }),
            (SourceUrl, "pkg-1", new VersionInfo { Tag = "v1.2.0", PublishedAt = "2025-12-01T00:00:00Z", IsPrerelease = false }),
            (SourceUrl, "pkg-2", new VersionInfo { Tag = "v0.1.0", PublishedAt = "2024-01-01T00:00:00Z", IsPrerelease = false }),
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
        SeedCatalog("node-1", "pkg-1");
        var items = Enumerable.Range(1, 12).Select(i => (
            SourceUrl,
            "pkg-1",
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
        SeedCatalog("n", "pkg-n");
        // 第一次写
        _repo.UpsertBatch(new[]
        {
            (SourceUrl, "pkg-n", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2024-01-01T00:00:00Z" }),
            (SourceUrl, "pkg-n", new VersionInfo { Tag = "v2.0.0", PublishedAt = "2025-01-01T00:00:00Z" }),
        });
        // 第二次写(覆盖) — 只剩新的
        _repo.UpsertBatch(new[]
        {
            (SourceUrl, "pkg-n", new VersionInfo { Tag = "v3.0.0", PublishedAt = "2026-01-01T00:00:00Z" }),
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
        SeedCatalog("n", "pkg-n");
        _repo.UpsertBatch(new[]
        {
            (SourceUrl, "pkg-n", new VersionInfo { Tag = "v2.0.0-rc1", PublishedAt = "2025-06-01T00:00:00Z", IsPrerelease = true }),
        });
        var list = _repo.ListByNode("n");
        Assert.Single(list);
        Assert.True(list[0].IsPrerelease);
        Assert.Contains("预发布", list[0].DisplayLabel);
    }

    /// <summary>
    /// v0.6.14 hotfix: UpsertBatch 按 (source_url, package) 寻址 —— catalog_cache 里
    /// 找不到对应 row 的 tuple 必须被静默跳过(不抛、不写脏数据)。
    /// </summary>
    [Fact]
    public void UpsertBatch_CatalogRowMissing_SilentlySkips()
    {
        // 没 SeedCatalog — (SourceUrl, "pkg-missing") 在 catalog_cache 里不存在
        var n = _repo.UpsertBatch(new[] {
            (SourceUrl, "pkg-missing",
                new VersionInfo { Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }),
        });
        Assert.Equal(0, n);
    }

    /// <summary>
    /// v0.6.14 hotfix: 当 catalog row 的 id 跟历史 node_id 不一致时(如
    /// CatalogFetcher 重新分配了新 GUID 且 row 被删除+重新插入,或未来 schema
    /// 改动允许 id 更新),UpsertBatch 必须仍然把 versions 写到当前 catalog row
    /// 的 node_id,而不是用旧的 node_id。
    /// 注:catalog_cache.UpsertBatch 的 ON CONFLICT(source_url, package) DO UPDATE
    /// 不更新 id 列,所以不能仅靠 upsert 改 id。这里用 DeleteRemovedEntriesAsync + 重新
    /// upsert 来模拟 id 切换(catalog row 被物理删除再插入,id 变为 fresh GUID)。
    /// </summary>
    [Fact]
    public void UpsertBatch_NewCatalogGuid_StillWritesToCurrentNodeId()
    {
        SeedCatalog("old-guid", "pkg-x");

        // 先用 old-guid 写一行
        _repo.UpsertBatch(new[] {
            (SourceUrl, "pkg-x",
                new VersionInfo { Tag = "v1.0.0", PublishedAt = "2025-01-01T00:00:00Z", IsPrerelease = false }),
        });
        Assert.Single(_repo.ListByNode("old-guid"));

        // 模拟 catalog row 被硬删 + 重新插入(用新 GUID) — DeleteRemovedEntriesAsync
        // 会把 old-guid 的 node_versions 也 cascade 删掉
        _catRepo.DeleteRemovedEntriesAsync(SourceUrl, new[] { "pkg-x" }).Wait();
        SeedCatalog("new-guid", "pkg-x");

        // sanity: catalog row 现在的 id 是 new-guid,old-guid 的 node_versions 已 cascade 删
        Assert.Equal("new-guid", _catRepo.Search("pkg-x", 10).Single().Id);
        Assert.Empty(_repo.ListByNode("old-guid"));

        // 现在用 (source_url, package) 写 — repository 内部应该查到 new-guid,
        // INSERT 到 new-guid
        _repo.UpsertBatch(new[] {
            (SourceUrl, "pkg-x",
                new VersionInfo { Tag = "v2.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }),
        });

        // new-guid 拿到新版本,old-guid 无 row
        Assert.Single(_repo.ListByNode("new-guid"));
        Assert.Equal("v2.0.0", _repo.ListByNode("new-guid")[0].Tag);
        Assert.Empty(_repo.ListByNode("old-guid"));
    }

    // v0.6.14 hotfix:Count() 给 CatalogRefreshService backfill 检测用 — 表空 + 开关 ON → 全量拉。

    [Fact]
    public void Count_EmptyTable_ReturnsZero()
    {
        Assert.Equal(0, _repo.Count());
    }

    [Fact]
    public void Count_AfterInserts_ReturnsRowCountAcrossNodes()
    {
        SeedCatalog("node-a", "pkg-a");
        SeedCatalog("node-b", "pkg-b");
        SeedCatalog("node-c", "pkg-c");
        _repo.UpsertBatch(new (string, string, VersionInfo)[]
        {
            (SourceUrl, "pkg-a", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false }),
            (SourceUrl, "pkg-a", new VersionInfo { Tag = "v0.9.0", PublishedAt = "2025-12-01T00:00:00Z", IsPrerelease = false }),
            (SourceUrl, "pkg-b", new VersionInfo { Tag = "v1.0.0", PublishedAt = "2026-02-01T00:00:00Z", IsPrerelease = false }),
        });
        Assert.Equal(3, _repo.Count());
    }
}
