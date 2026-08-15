using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15 T5:测 <see cref="CatalogViewModel.IsInLocalNodeDbFor"/> —
/// 查 package (nodeId) 是否已下载到本地节点目录
/// (看 scanned_nodes 中 EnvId="" + Source="download" 行)。
/// 不依赖 null ctor 注入的脆弱 pattern(原 brief 写法会让 ctor 末尾的
/// <c>Search()</c> NRE),改用真实 <see cref="CatalogRepository"/> + <see cref="NodeRepository"/>。
/// </summary>
public class CatalogViewModelIsInLocalNodeDbTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _projectRoot;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly FakeRefreshService _refreshService;
    private readonly NoopNodeOps _nodeOps;
    private readonly NodeRepository _nodeRepo;

    public CatalogViewModelIsInLocalNodeDbTests()
    {
        _db = new TestDb();
        _projectRoot = Path.Combine(
            Path.GetTempPath(), $"cat-vm-lndb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, _projectRoot);
        _settingsRepo = new SettingsRepository(
            Path.Combine(_projectRoot, "settings.json"));
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
        _refreshService = new FakeRefreshService();
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new NoopNodeOps(
            new EnvironmentRepository(_db.Factory),
            _nodeRepo, _settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(
            _catRepo, _versionRepo, _nodeOps, _refreshService,
            _settings, _settingsRepo, _projectRoot,
            rateLimitState: null, nodeRepo: _nodeRepo);

    [Fact]
    public void IsInLocalNodeDbFor_NoDownloadRow_ReturnsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsInLocalNodeDbFor("pkg-a"));
    }

    [Fact]
    public void IsInLocalNodeDbFor_HasDownloadRow_ReturnsTrue()
    {
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = "pkg-b",
            EnvId = "",
            Source = "download",
            Package = "pkg-b",
        });
        var vm = NewVm();
        Assert.True(vm.IsInLocalNodeDbFor("pkg-b"));
    }

    [Fact]
    public void IsInLocalNodeDbFor_HasEnvRowOnly_ReturnsFalse()
    {
        // 装到 env 里的 Source="env" 行不应被识别为"已下载到本地节点目录"。
        // IsInLocalNodeDbFor 严格看 Source="download" 的 sentinel 行。
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = "pkg-c",
            EnvId = "env-1",
            Source = "env",
            Package = "pkg-c",
        });
        var vm = NewVm();
        Assert.False(vm.IsInLocalNodeDbFor("pkg-c"));
    }

    [Fact]
    public void IsInLocalNodeDbFor_NullNodeRepo_ReturnsFalse()
    {
        // 不传 nodeRepo(nodeRepo: null)→ 内部 _nodeRepo is null → 返回 false,
        // 不抛 NRE。覆盖老 ctor 路径(MainViewModel 老调用方未传 nodeRepo 时)。
        var vm = new CatalogViewModel(
            _catRepo, _versionRepo, _nodeOps, _refreshService,
            _settings, _settingsRepo, _projectRoot);
        Assert.False(vm.IsInLocalNodeDbFor("pkg-any"));
    }

    /// <summary>
    /// 简单 stub <see cref="CatalogRefreshService"/>,所有 RefreshAsync 调用
    /// 返回空 OK。复用 <see cref="CatalogViewModelTests"/> 的 fake 模式(独立
    /// 内嵌类避免跨测试文件耦合)。
    /// </summary>
    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            System.Threading.CancellationToken ct = default)
            => Task.FromResult(RefreshResult.Ok(0));

        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override Task<CatalogFetchResult> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")))) { }
    }
}
