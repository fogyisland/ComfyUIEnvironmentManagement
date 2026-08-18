using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.16+:CatalogViewModel 详情面板新增 SelectedDeveloper + SelectedDevelopedAt
/// (用户原话:"版本号、开发者、开发日期、版本日期"是最重要的)。
///
/// - SelectedDeveloper 从 html_url 第一段路径解析(GitHub owner login)
/// - SelectedDevelopedAt 从 created_at 截短到 yyyy-MM-dd
///
/// html_url / created_at 来自 catalog entry 模型已有字段;新增 Python backfill
/// (任务 #179)填进去后即生效 — 不需要 schema migration。
/// </summary>
public class CatalogViewModelSelectedMetaTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;
    private readonly FakeRefreshService _refreshService;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;

    public CatalogViewModelSelectedMetaTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _settingsRepo = new SettingsRepository(Path.Combine(
            Path.GetTempPath(), $"cat-meta-{Guid.NewGuid():N}.json"));
        _refreshService = new FakeRefreshService();
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
    }

    public void Dispose() => _db.Dispose();

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(_catRepo, _versionRepo,
            new NoopNodeOps(new EnvironmentRepository(_db.Factory),
                            new NodeRepository(_db.Factory), _settings),
            _refreshService, _settings, _settingsRepo, @"D:\ToolDevelop\ComfyUI");

    private void Seed(string package, string? htmlUrl, string? createdAt)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            CachedAt = "2026-08-17T00:00:00",
            ExpiresAt = "2027-08-17T00:00:00",
            HtmlUrl = htmlUrl,
            CreatedAt = createdAt,
        });
    }

    // ─────────────── SelectedDeveloper ────────────────

    [Fact]
    public void SelectedDeveloper_ParsesOwnerFromHtmlUrl()
    {
        Seed("pkg-a", "https://github.com/ltdrdata/ComfyUI-Manager", "2023-01-15T10:00:00Z");

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-a");

        Assert.Equal("ltdrdata", vm.SelectedDeveloper);
    }

    [Fact]
    public void SelectedDeveloper_StripsWwwAndTrailingSlash()
    {
        Seed("pkg-b", "https://github.com/comfyanonymous/ComfyUI_examples/", "2024-05-01T00:00:00Z");

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-b");

        Assert.Equal("comfyanonymous", vm.SelectedDeveloper);
    }

    [Fact]
    public void SelectedDeveloper_EmptyHtmlUrl_ReturnsUnknown()
    {
        Seed("pkg-c", null, null);

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-c");

        Assert.Equal("未知", vm.SelectedDeveloper);
    }

    [Fact]
    public void SelectedDeveloper_NoSelection_ReturnsUnknown()
    {
        // 不设 Selected,直接读 — 应当返 "未知"(没东西可解析)而不是 NRE
        var vm = NewVm();
        Assert.Null(vm.Selected);

        // _selected is null → SelectedDeveloper getter 走 _selected?.HtmlUrl 短路 → null
        // 我们的实现返 "未知" 兜底,所以非 null
        Assert.Equal("未知", vm.SelectedDeveloper);
    }

    // ─────────────── SelectedDevelopedAt ────────────────

    [Fact]
    public void SelectedDevelopedAt_TrimsIsoTimestamp_ToYYYYMMDD()
    {
        Seed("pkg-d", "https://github.com/x/y", "2025-06-15T10:30:45Z");

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-d");

        Assert.Equal("2025-06-15", vm.SelectedDevelopedAt);
    }

    [Fact]
    public void SelectedDevelopedAt_EmptyCreatedAt_ReturnsUnknown()
    {
        Seed("pkg-e", "https://github.com/x/y", null);

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-e");

        Assert.Equal("未知", vm.SelectedDevelopedAt);
    }

    [Fact]
    public void SelectedDevelopedAt_ShortString_ReturnsUnknown()
    {
        Seed("pkg-f", "https://github.com/x/y", "2025");

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-f");

        Assert.Equal("未知", vm.SelectedDevelopedAt);
    }

    [Fact]
    public void SelectedDevelopedAt_BothFieldsPopulated_DeveloperAndDateCoexist()
    {
        // 综合测试:开发者和开发日期来自同一 entry,两个 property 互不干扰
        Seed("pkg-g", "https://github.com/WASasquatch/FreeU_Advanced", "2023-09-23T08:00:00Z");

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-g");

        Assert.Equal("WASasquatch", vm.SelectedDeveloper);
        Assert.Equal("2023-09-23", vm.SelectedDevelopedAt);
    }

    [Fact]
    public void Selected_Switching_RaisesBothMetaProperties()
    {
        // 切换 Selected 必须 RaisePropertyChanged 这两个新字段,否则详情面板不刷
        // (Selected setter 调用 RaisePropertyChanged 列表已包含名字)
        Seed("pkg-h1", "https://github.com/owner-a/repo", "2023-01-01T00:00:00Z");
        Seed("pkg-h2", "https://github.com/owner-b/repo", "2024-06-15T00:00:00Z");

        var vm = NewVm();
        var first = vm.PagedEntries.First(e => e.Package == "pkg-h1");
        var second = vm.PagedEntries.First(e => e.Package == "pkg-h2");

        vm.Selected = first;
        Assert.Equal("owner-a", vm.SelectedDeveloper);
        Assert.Equal("2023-01-01", vm.SelectedDevelopedAt);

        vm.Selected = second;
        Assert.Equal("owner-b", vm.SelectedDeveloper);
        Assert.Equal("2024-06-15", vm.SelectedDevelopedAt);
    }

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override System.Threading.Tasks.Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            System.Threading.CancellationToken ct = default)
        {
            return System.Threading.Tasks.Task.FromResult(RefreshResult.Ok(0));
        }

        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override System.Threading.Tasks.Task<CatalogFetchResult> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => System.Threading.Tasks.Task.FromResult(new ProcessResult(true, 0, "[]", "")))) { }
    }
}
