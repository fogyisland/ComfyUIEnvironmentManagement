using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
/// v0.6.15.2 hotfix: <see cref="CatalogViewModel"/> 的 repo URL 提取 5 路 fallback
/// + <c>SelectedRepositoryUrl</c> / <c>SelectedLatestVersion</c> 两个新显示属性。
///
/// 上游 ltdrdata custom-node-list.json 的字段差异很大:多数 entry 没有显式
/// <c>repository</c> / <c>url</c>,而是 <c>files[]</c> / <c>reference</c> / <c>id</c>(owner/repo)。
/// ExtractRepoUrl 必须按优先级正确 fallback,否则 DownloadAsync 误报
/// "catalog 条目缺 repository url" 让用户没法装。
/// </summary>
public sealed class CatalogViewModelExtractRepoUrlTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _settingsRepoPath;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly FakeRefreshService _refreshService;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _projectRoot;

    public CatalogViewModelExtractRepoUrlTests()
    {
        _db = new TestDb();
        _projectRoot = Path.Combine(Path.GetTempPath(), $"cat-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, _projectRoot);
        _settingsRepoPath = Path.Combine(Path.GetTempPath(), $"cat-ext-{Guid.NewGuid():N}.json");
        _settingsRepo = new SettingsRepository(_settingsRepoPath);
        _refreshService = new FakeRefreshService();
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, true); } catch { }
    }

    /// <summary>
    /// Seed 一个 catalog entry,带指定 raw_metadata(string 值而非 JsonElement),
    /// upsert 后从 DB 读回(读回的 RawMetadata 里 string 会变 JsonElement,
    /// 测试要手动还原成 string 给 ExtractRepoUrl 的 <c>is string</c> 检查用)。
    /// </summary>
    private CatalogEntry SeedEntry(string package, Dictionary<string, object?> rawMeta)
    {
        var entry = new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            RawMetadata = new Dictionary<string, object?>(rawMeta),
            CachedAt = "2026-07-13T00:00:00",
            ExpiresAt = "2027-07-13T00:00:00",
        };
        _catRepo.Upsert(entry);
        return _catRepo.Search("", limit: 0).Find(e => e.Package == package)!;
    }

    /// <summary>
    /// 把从 DB 读回的 CatalogEntry.RawMetadata 里所有 JsonElement string 还原成 CLR string。
    /// </summary>
    private static void FixRawMetadataToStrings(CatalogEntry entry)
    {
        if (entry.RawMetadata.Count == 0) return;
        var fixed_ = new Dictionary<string, object?>();
        foreach (var (k, v) in entry.RawMetadata)
        {
            if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                fixed_[k] = je.GetString();
            else
                fixed_[k] = v;
        }
        entry.RawMetadata.Clear();
        foreach (var (k, v) in fixed_) entry.RawMetadata[k] = v;
    }

    private CatalogViewModel NewVm() =>
        new(_catRepo, _versionRepo, new CapturingNodeOps(), _refreshService, _settings, _settingsRepo, _projectRoot);

    [Fact]
    public void SelectedRepositoryUrl_RepositoryField_TakesPrecedence()
    {
        var entry = SeedEntry("pkg-1", new Dictionary<string, object?>
        {
            ["repository"] = "https://github.com/owner/pkg-1.git",
            ["files"] = new List<object?> { "https://github.com/wrong/pkg-1.git" },
            ["reference"] = "https://github.com/wrong2/pkg-1",
            ["id"] = "wrong3/pkg-1",
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/owner/pkg-1.git", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_UrlField_SecondPriority()
    {
        var entry = SeedEntry("pkg-2", new Dictionary<string, object?>
        {
            ["url"] = "https://github.com/owner/pkg-2.git",
            ["files"] = new List<object?> { "https://github.com/wrong/pkg-2.git" },
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/owner/pkg-2.git", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_FilesFirstElement_ThirdPriority()
    {
        // 上游 ltdrdata 主流形态:只有 files[],没 repository/url/reference/id
        var entry = SeedEntry("pkg-3", new Dictionary<string, object?>
        {
            ["files"] = new List<object?> { "https://github.com/ltdrdata/ComfyUI-Impact-Pack" },
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Impact-Pack", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_FilesJsonElementArray_HandledByFirstStringElement()
    {
        // 模拟 JsonElement 残留:某些路径下 files 数组元素仍是 JsonElement(测试 helper 已转换,
        // 但 production 路径通过 upsert→read 回可能保留)。这里直接构造 JsonElement。
        var arr = System.Text.Json.JsonDocument.Parse(
            """["https://github.com/from-json-element/repo"]""").RootElement;
        var entry = SeedEntry("pkg-3b", new Dictionary<string, object?> { ["files"] = arr });
        // 不调 FixRawMetadataToStrings —— 验证 JsonElement 路径也走通

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/from-json-element/repo", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_ReferenceGithubUrl_FourthPriority()
    {
        var entry = SeedEntry("pkg-4", new Dictionary<string, object?>
        {
            ["reference"] = "https://github.com/ltdrdata/ComfyUI-Manager",
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_ReferenceNonGithubUrl_IgnoredThenFallsBackToId()
    {
        // reference 不是 github URL(比如文档站),应跳过走 id fallback
        var entry = SeedEntry("pkg-4b", new Dictionary<string, object?>
        {
            ["reference"] = "https://custom-docs.example.com/pkg",
            ["id"] = "owner/pkg-4b",
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/owner/pkg-4b", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_IdOwnerRepo_ExpandedToGithubUrl()
    {
        // 上游 catalog 偶尔只有 id 是 owner/repo
        var entry = SeedEntry("pkg-5", new Dictionary<string, object?>
        {
            ["id"] = "comfyanonymous/ComfyUI",
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("https://github.com/comfyanonymous/ComfyUI", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_NoRelevantFields_ReturnsEmpty()
    {
        // 只有 author/title,跟第一手 CatalogEntry 一样空
        var entry = SeedEntry("pkg-empty", new Dictionary<string, object?>
        {
            ["author"] = "nobody",
            ["title"] = "Anonymous",
        });
        FixRawMetadataToStrings(entry);

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedRepositoryUrl_NoSelected_ReturnsEmpty()
    {
        var vm = NewVm();
        Assert.Null(vm.Selected);
        Assert.Equal("", vm.SelectedRepositoryUrl);
    }

    [Fact]
    public void SelectedLatestVersion_EmptyString_ReturnsUnknown()
    {
        var entry = SeedEntry("pkg-no-ver", new Dictionary<string, object?>
        {
            ["title"] = "no-version",
        });
        FixRawMetadataToStrings(entry);
        // 不设 LatestVersion → 留空

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("未知", vm.SelectedLatestVersion);
    }

    [Fact]
    public void SelectedLatestVersion_SetString_ReturnsItVerbatim()
    {
        var entry = SeedEntry("pkg-ver", new Dictionary<string, object?>
        {
            ["title"] = "has-version",
        });
        FixRawMetadataToStrings(entry);
        entry.LatestVersion = "v2.5.0";

        var vm = NewVm();
        vm.Selected = entry;

        Assert.Equal("v2.5.0", vm.SelectedLatestVersion);
    }

    [Fact]
    public async Task DownloadCommand_UsesFilesFirstElement_WhenRepositoryMissing()
    {
        var ops = new CapturingNodeOps();
        var vm = new CatalogViewModel(_catRepo, _versionRepo, ops, _refreshService, _settings, _settingsRepo, _projectRoot);
        var entry = SeedEntry("pkg-files-fallback", new Dictionary<string, object?>
        {
            ["files"] = new List<object?> { "https://github.com/owner/pkg-files-fallback" },
        });
        FixRawMetadataToStrings(entry);
        vm.Selected = entry;

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(1, ops.CallCount);
        Assert.Equal("https://github.com/owner/pkg-files-fallback", ops.CalledRepoUrl);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task DownloadCommand_UsesIdExpanded_WhenOnlyIdPresent()
    {
        var ops = new CapturingNodeOps();
        var vm = new CatalogViewModel(_catRepo, _versionRepo, ops, _refreshService, _settings, _settingsRepo, _projectRoot);
        var entry = SeedEntry("pkg-id-only", new Dictionary<string, object?>
        {
            ["id"] = "owner/pkg-id-only",
        });
        FixRawMetadataToStrings(entry);
        vm.Selected = entry;

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(1, ops.CallCount);
        Assert.Equal("https://github.com/owner/pkg-id-only", ops.CalledRepoUrl);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>
    /// 抓 DownloadAsync 调用的实参(同 CatalogViewModelDownloadTests.CapturingNodeOps pattern)。
    /// </summary>
    private sealed class CapturingNodeOps : NodeOperations
    {
        public string? CalledRepoUrl { get; private set; }
        public int CallCount { get; private set; }

        public CapturingNodeOps()
            : base(new GitRunner("git"),
                   new EnvironmentRepository(new TestDb().Factory),
                   new NodeRepository(new TestDb().Factory),
                   new Settings(),
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ComfyUI.Manager.Infrastructure.ProcessResult(true, 0, "[]", ""))))
        { }

        public override Task<NodeOperationResult> DownloadAsync(
            string localDir, string nodeId, string repoUrl,
            string? targetTag = null,
            CancellationToken ct = default)
        {
            CallCount++;
            CalledRepoUrl = repoUrl;
            return Task.FromResult(NodeOperationResult.Ok("abc123"));
        }
    }

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-ext-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
            => Task.FromResult(RefreshResult.Ok(0));

        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override Task<CatalogFetchResult> FetchAsync(
                string url, CancellationToken ct = default)
                => throw new NotImplementedException();
        }
    }
}