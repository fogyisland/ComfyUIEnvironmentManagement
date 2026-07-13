using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
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

    [Fact]
    public void Ctor_LoadsAllCatalogEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "pkg-a");
        SeedCatalog(db, "pkg-b");

        var vm = new CatalogViewModel(new CatalogRepository(db.Factory));

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Query_FiltersEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "alpha");
        SeedCatalog(db, "beta");

        var vm = new CatalogViewModel(new CatalogRepository(db.Factory));
        vm.Query = "alph";

        Assert.Single(vm.Entries);
        Assert.Equal("alpha", vm.Entries[0].Package);
    }
}
