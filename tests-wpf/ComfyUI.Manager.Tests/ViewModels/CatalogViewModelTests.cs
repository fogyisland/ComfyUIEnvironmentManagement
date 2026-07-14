using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelTests
{
    private static void SeedCatalog(TestDb db, string package)
    {
        var repo = new CatalogRepository(db.Factory);
        repo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = "https://example/registry",
            Package = package,
            CachedAt = "2026-07-13T00:00:00",
            ExpiresAt = "2027-07-13T00:00:00",
        });
    }

    /// <summary>
    /// Noop NodeOperations:不会真跑 git clone。Catalog 页面测试不需要 git。
    /// </summary>
    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo)
            : base(new GitRunner("git"), envRepo, nodeRepo)
        {
        }
    }

    [Fact]
    public void Ctor_LoadsAllCatalogEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "pkg-a");
        SeedCatalog(db, "pkg-b");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)));

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Query_FiltersEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "alpha");
        SeedCatalog(db, "beta");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)));
        vm.Query = "alph";

        Assert.Single(vm.Entries);
        Assert.Equal("alpha", vm.Entries[0].Package);
    }
}
