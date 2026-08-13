using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15:CatalogViewModel 的 4 progress callback + RateLimitBanner 触发路径。
/// 用 fake CatalogRefreshService (override RefreshAsync 直接调 IProgress.Report)
/// 触发 VM 内部 progress handler,验 4 个 progress string + banner 状态变化。
/// </summary>
public class CatalogViewModelProgressTests : IDisposable
{
    private readonly string _projectRoot;

    public CatalogViewModelProgressTests()
    {
        _projectRoot = Path.Combine(
            Path.GetTempPath(), $"cvm-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, true); } catch { }
    }

    /// <summary>
    /// Fake CatalogRefreshService override RefreshAsync 直接发 progress event,
    /// 让 VM 内部 Progress&lt;T&gt; 回调触发,验 4 个 progress string 同步更新。
    /// </summary>
    private sealed class FakeCatalogRefreshService : CatalogRefreshService
    {
        public Action<
            IProgress<CatalogEntry>?,
            IProgress<VersionFetchProgress>?,
            IProgress<RateLimitInfo>?,
            IProgress<MetadataFetchProgress>?,
            IRateLimitState?,
            CancellationToken>? OnRefresh { get; set; }

        public FakeCatalogRefreshService()
            : base(
                new CatalogFetcher(new HttpClient(), 60, null),
                new CatalogRepository(new CatalogCacheStore(Path.Combine(
                    Path.GetTempPath(), $"fake-crs-{Guid.NewGuid():N}.db"))),
                new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            OnRefresh?.Invoke(progress, versionProgress, rateLimitProgress,
                metadataProgress, rateLimitState, ct);
            return Task.FromResult(RefreshResult.Ok(
                n: 5, v: 0, m: 0,
                added: 5, updated: 0, skipped: 100, deleted: 0));
        }
    }

    /// <summary>
    /// 最小可用的 NodeOperations —— 不真跑 git,只让 VM 构造过。
    /// </summary>
    private static NodeOperations MakeNodeOps()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cvm-progress-ops-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(dbPath);
        return new NodeOperations(
            new GitRunner("git"),
            new EnvironmentRepository(factory),
            new NodeRepository(factory),
            new Settings(),
            new NodeInstallDiffService(
                (_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
    }

    private static (CatalogViewModel vm, FakeCatalogRefreshService fake)
        CreateVm()
    {
        var (vm, fake, _) = CreateVmWithRepo();
        return (vm, fake);
    }

    private static (CatalogViewModel vm, FakeCatalogRefreshService fake,
        CatalogRepository repo) CreateVmWithRepo()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cvm-progress-{Guid.NewGuid():N}.db");
        var cacheStore = new CatalogCacheStore(dbPath);
        var catRepo = new CatalogRepository(cacheStore);
        var verRepo = new NodeVersionRepository(cacheStore);
        var nodeOps = MakeNodeOps();
        var fake = new FakeCatalogRefreshService();
        var settingsRepo = new SettingsRepository();
        var state = new RateLimitState();
        var vm = new CatalogViewModel(
            catRepo, verRepo, nodeOps, fake, new Settings(), settingsRepo,
            Path.GetTempPath(), rateLimitState: state);
        return (vm, fake, catRepo);
    }

    [Fact]
    public async Task RefreshAsync_Updates4ProgressProperties_OnCallbacks()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (p, vp, rlp, mp, _, _) =>
        {
            p?.Report(new CatalogEntry { Id = "e1", Package = "p1" });
            p?.Report(new CatalogEntry { Id = "e2", Package = "p2" });
            vp?.Report(new VersionFetchProgress(Completed: 50, Total: 100, CurrentNodeId: "e1"));
            mp?.Report(new MetadataFetchProgress(Done: 25, Total: 100, CurrentPackage: "p1"));
        };

        await vm.RefreshAsync();
        // 等 Progress<T> 回调 marshal 完成 —— 无 SyncContext 时走 ThreadPool
        await Task.Delay(100);

        Assert.Equal("拉取 catalog: 2 entries", vm.ReadProgress);
        Assert.Equal("拉取版本: 50/100", vm.VersionProgress);
        Assert.Equal("拉取 metadata: 25/100", vm.MetadataProgress);
        Assert.Contains("+5", vm.WriteProgress);
    }

    [Fact]
    public async Task RefreshAsync_RateLimitInfo_ShowsBanner()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, rlp, _, _, _) =>
        {
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, Remaining: 0,
                ResetUnix: DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                PartialCount: 100, TotalCount: 5000));
        };

        await vm.RefreshAsync();
        await Task.Delay(100);

        Assert.True(vm.RateLimitBanner.IsVisible);
        Assert.Contains("节点版本", vm.RateLimitBanner.Title);
    }

    [Fact]
    public async Task RefreshAsync_Start_HidesBanner()
    {
        var (vm, fake) = CreateVm();
        // 先手动 Show banner
        vm.RateLimitBanner.Show(
            new RateLimitInfo(RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), 100, 5000),
            DateTimeOffset.Now);
        Assert.True(vm.RateLimitBanner.IsVisible);
        // Refresh 入口 Hide
        fake.OnRefresh = (_, _, _, _, _, _) => { /* no-op */ };
        await vm.RefreshAsync();

        Assert.False(vm.RateLimitBanner.IsVisible);
    }

    [Fact]
    public async Task RefreshAsync_Complete_InfoMessageIncludes4Counts()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, _, _, _, _) => { /* no-op */ };
        await vm.RefreshAsync();

        Assert.Contains("+5", vm.WriteProgress);
        Assert.Contains("⟳100", vm.WriteProgress);
        Assert.Contains("+5", vm.InfoMessage);
        Assert.Contains("⟳100", vm.InfoMessage);
    }

    [Fact]
    public async Task RefreshAsync_MultipleRateLimitHits_TakesLatest()
    {
        var (vm, fake) = CreateVm();
        fake.OnRefresh = (_, _, rlp, _, _, _) =>
        {
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
                100, 5000));
            rlp?.Report(new RateLimitInfo(
                RateLimitStage.Version, 0,
                DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds(),
                200, 5500));
        };

        await vm.RefreshAsync();
        await Task.Delay(100);

        Assert.True(vm.RateLimitBanner.IsVisible);
        Assert.Contains("200/5500", vm.RateLimitBanner.Message);
    }

    /// <summary>
    /// 命中 304 Not Modified 时 refresh service 一条 entry 都不 report,
    /// 列表必须仍从 DB 重读回来,而不是留在 refresh 入口清空后的空状态。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NoEntriesReported_RestoresListFromDb()
    {
        var (vm, fake, repo) = CreateVmWithRepo();
        repo.Upsert(new CatalogEntry { Id = "e1", Package = "p1" });
        repo.Upsert(new CatalogEntry { Id = "e2", Package = "p2" });
        fake.OnRefresh = (_, _, _, _, _, _) => { /* 304:一条都不 report */ };

        await vm.RefreshAsync();

        Assert.True(vm.HasEntries);
        Assert.Equal(2, vm.PagedEntries.Count);
    }
}
