using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _settingsRepoPath;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly FakeRefreshService _refreshService;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NoopNodeOps _nodeOps;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;

    public CatalogViewModelTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _settingsRepoPath = Path.Combine(
            Path.GetTempPath(), $"cat-vm-{Guid.NewGuid():N}.json");
        _settingsRepo = new SettingsRepository(_settingsRepoPath);
        _refreshService = new FakeRefreshService();
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new NoopNodeOps(_envRepo, _nodeRepo, _settings);
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
    }

    public void Dispose() => _db.Dispose();

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(_catRepo, _versionRepo, _envRepo, _nodeOps, _refreshService, _settings, _settingsRepo);

    private void SeedCatalog(string package)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            CachedAt = "2026-07-13T00:00:00",
            ExpiresAt = "2027-07-13T00:00:00",
        });
    }

    private void SeedVersions(string nodeId, params (string Tag, string Date, bool Pre)[] versions)
    {
        _versionRepo.UpsertBatch(versions.Select(v => (
            nodeId,
            new VersionInfo { Tag = v.Tag, PublishedAt = v.Date, IsPrerelease = v.Pre })));
    }

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public RefreshResult NextResult { get; set; } =
            RefreshResult.Ok(0);
        public int RefreshCallCount { get; private set; }

        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<ComfyUI.Manager.Models.CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            System.Threading.CancellationToken ct = default)
        {
            RefreshCallCount++;
            return Task.FromResult(NextResult);
        }

        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override Task<List<CatalogEntry>> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings) { }
    }

    [Fact]
    public void Ctor_LoadsLocalCache_AsFirstPage_NoAutoRefresh()
    {
        SeedCatalog("pkg-a");
        SeedCatalog("pkg-b");

        var vm = NewVm();

        Assert.Equal(2, vm.PagedEntries.Count);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, _refreshService.RefreshCallCount);
    }

    [Fact]
    public void Query_FiltersAndResetsToFirstPage()
    {
        SeedCatalog("alpha");
        SeedCatalog("beta");

        var vm = NewVm();
        vm.Query = "alph";

        Assert.Single(vm.PagedEntries);
        Assert.Equal("alpha", vm.PagedEntries[0].Package);
        Assert.Equal(1, vm.CurrentPage);
    }

    [Fact]
    public void NextPageCommand_AdvancesPage_WhenMorePages()
    {
        for (var i = 0; i < 25; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();

        vm.NextPageCommand.Execute(null);

        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(5, vm.PagedEntries.Count);
    }

    [Fact]
    public void NextPageCommand_CannotExecute_OnLastPage()
    {
        for (var i = 0; i < 5; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();

        Assert.False(vm.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public void PrevPageCommand_CannotExecute_OnFirstPage()
    {
        SeedCatalog("pkg-a");
        var vm = NewVm();

        Assert.False(vm.PrevPageCommand.CanExecute(null));
    }

    [Fact]
    public void ViewMode_DefaultsFromSettings_List()
    {
        var vm = NewVm();
        Assert.Equal(CatalogViewMode.List, vm.ViewMode);
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsTileMode);
    }

    [Fact]
    public void SetTileViewCommand_PersistsToSettings()
    {
        var vm = NewVm();

        vm.SetTileViewCommand.Execute(null);

        Assert.Equal(CatalogViewMode.Tile, vm.ViewMode);
        Assert.True(vm.IsTileMode);
        Assert.False(vm.IsListMode);

        var reloaded = new SettingsRepository(_settingsRepoPath).Load();
        Assert.Equal(CatalogViewMode.Tile, reloaded.CatalogViewMode);
    }

    [Fact]
    public async Task RefreshCommand_DelegatesToRefreshService()
    {
        var vm = NewVm();

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(1, _refreshService.RefreshCallCount);
    }

    [Fact]
    public async Task RefreshCommand_Success_ShowsInfoMessageAndJumpsToFirstPage()
    {
        _refreshService.NextResult = RefreshResult.Ok(120);
        for (var i = 0; i < 21; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Contains("刷新成功,共 120 个条目", vm.InfoMessage);
    }

    [Fact]
    public async Task RefreshCommand_Failure_SetsErrorMessage()
    {
        _refreshService.NextResult = RefreshResult.Fail("拉取失败: dns fail");
        var vm = NewVm();

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Contains("拉取失败", vm.ErrorMessage);
    }

    [Fact]
    public void Selected_LoadsVersionsFromRepo_DescendingOrder()
    {
        SeedCatalog("pkg-a");
        SeedVersions("pkg-a",
            ("v1.0.0", "2025-01-01T00:00:00Z", false),
            ("v2.0.0", "2026-01-01T00:00:00Z", false),
            ("v1.5.0", "2025-06-01T00:00:00Z", false));

        var vm = NewVm();
        var entry = vm.PagedEntries.First(e => e.Package == "pkg-a");
        vm.Selected = entry;

        Assert.Equal(3, vm.SelectedVersions.Count);
        Assert.Equal("v2.0.0", vm.SelectedVersions[0].Tag);
        Assert.Equal("v1.5.0", vm.SelectedVersions[1].Tag);
        Assert.Equal("v1.0.0", vm.SelectedVersions[2].Tag);
        Assert.Same(vm.SelectedVersions[0], vm.SelectedVersion);  // 默认选最新
        Assert.Equal("2026-01-01", vm.SelectedVersionDate);
    }

    [Fact]
    public void Selected_NoVersions_LeavesCollectionsEmpty()
    {
        SeedCatalog("pkg-empty");
        var vm = NewVm();
        var entry = vm.PagedEntries.First(e => e.Package == "pkg-empty");
        vm.Selected = entry;

        Assert.Empty(vm.SelectedVersions);
        Assert.Null(vm.SelectedVersion);
        Assert.False(vm.HasVersions);
    }

    [Fact]
    public void SelectedVersionDate_PicksFirst10Chars()
    {
        SeedCatalog("pkg-x");
        SeedVersions("pkg-x", ("v1.0.0", "2025-07-15T10:30:00Z", false));

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-x");

        Assert.Equal("2025-07-15", vm.SelectedVersionDate);
    }

    [Fact]
    public void InstallButtonLabel_NoVersions_ReturnsInstall()
    {
        SeedCatalog("pkg-no-versions");
        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-no-versions");

        Assert.Equal("安装", vm.InstallButtonLabel);
    }

    [Fact]
    public void InstallButtonLabel_WithVersions_DefaultsToLatest()
    {
        SeedCatalog("pkg-with-versions");
        SeedVersions("pkg-with-versions",
            ("v1.0.0", "2025-01-01T00:00:00Z", false),
            ("v2.0.0", "2026-01-01T00:00:00Z", false));

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-with-versions");

        // ListByNode returns DESC by published_at → v2.0.0 排第一个 → 自动默认
        Assert.Equal("安装 v2.0.0", vm.InstallButtonLabel);
    }

    [Fact]
    public void InstallButtonLabel_UpdatesWhenSelectedVersionChanges()
    {
        SeedCatalog("pkg-switch");
        SeedVersions("pkg-switch",
            ("v1.0.0", "2025-01-01T00:00:00Z", false),
            ("v2.0.0", "2026-01-01T00:00:00Z", false));

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-switch");

        Assert.Equal("安装 v2.0.0", vm.InstallButtonLabel);

        vm.SelectedVersion = vm.SelectedVersions.Last();  // 切到 v1.0.0
        Assert.Equal("安装 v1.0.0", vm.InstallButtonLabel);

        vm.SelectedVersion = vm.SelectedVersions.First();  // 切回 v2.0.0
        Assert.Equal("安装 v2.0.0", vm.InstallButtonLabel);
    }

    [Fact]
    public void InstallButtonLabel_ClearedWhenSelectionCleared()
    {
        SeedCatalog("pkg-clear");
        SeedVersions("pkg-clear", ("v9.9.9", "2025-06-01T00:00:00Z", false));

        var vm = NewVm();
        var entry = vm.PagedEntries.First(e => e.Package == "pkg-clear");
        vm.Selected = entry;
        Assert.Equal("安装 v9.9.9", vm.InstallButtonLabel);

        vm.Selected = null;
        Assert.Equal("安装", vm.InstallButtonLabel);
    }
}