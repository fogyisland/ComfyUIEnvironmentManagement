using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Moq;
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

    /// <summary>
    /// Test double for CatalogFetcher: returns preset entries or throws preset exception,
    /// records the last URL passed. Used by RefreshAsync tests to avoid real HTTP.
    /// </summary>
    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }
        public string? LastUrl { get; private set; }

        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60)
        {
        }

        public override async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        {
            LastUrl = url;
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            return await Task.FromResult(EntriesToReturn);
        }
    }

    [Fact]
    public void Ctor_LoadsAllCatalogEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "pkg-a");
        SeedCatalog(db, "pkg-b");

        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Query_FiltersEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "alpha");
        SeedCatalog(db, "beta");

        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);
        vm.Query = "alph";

        Assert.Single(vm.Entries);
        Assert.Equal("alpha", vm.Entries[0].Package);
    }

    [Fact]
    public async Task RefreshAsync_FetchesFromActiveQuerySource_AndUpserts()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Package = "from-active-source" },
            },
        };

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            fetcher,
            settings);

        vm.RefreshCommand.Execute(null);
        // RefreshCommand currently wraps Refresh(); Refresh becomes RefreshAsync and
        // RefreshCommand.Execute wraps an async lambda. Wait briefly for completion:
        await Task.Delay(200);

        Assert.Equal(settings.QuerySources[0].Url, fetcher.LastUrl);
        var entries = new CatalogRepository(db.Factory).Search("", 10);
        Assert.Single(entries);
        Assert.Equal("from-active-source", entries[0].Package);
        Assert.Equal(settings.QuerySources[0].Url, entries[0].SourceUrl);
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_SetsErrorMessage()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings
        {
            QuerySources = new(),  // 列表也空
            ActiveQuerySourceName = "nonexistent",
        };
        // 不跑 SettingsDefaults.Apply,settings 保持空 query_sources + 错误 active name

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(100);

        Assert.Contains("未配置查询源", vm.ErrorMessage);
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailure_SetsErrorMessageAndSearchesLocal()
    {
        using var db = new TestDb();
        SeedCatalog(db, "cached-pkg");  // 本地 cache 已有一条
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            fetcher,
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(100);

        Assert.Contains("拉取失败", vm.ErrorMessage);
        // 本地 cache 仍在
        Assert.Single(vm.Entries);
        Assert.Equal("cached-pkg", vm.Entries[0].Package);
    }

    [Fact]
    public async Task RefreshAsync_ClearsIsBusy_OnCompletion()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(200);

        Assert.False(vm.IsBusy);
    }
}