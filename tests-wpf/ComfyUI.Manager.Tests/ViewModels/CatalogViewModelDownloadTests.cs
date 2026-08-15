using System;
using System.Collections.Generic;
using System.IO;
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
/// v0.6.5.9 T3:Catalog 主页「下载」按钮的 CatalogViewModel 行为。
///
/// 关键不变量:
/// - DownloadCommand 调 NodeOperations.DownloadAsync(localDir, nodeId, repoUrl, tag)
/// - localDir = <c>Path.Combine(projectRoot, settings.LocalNodeDirectory)</c>
/// - 缺 repoUrl / 缺 localDir 时,VM 自己弹出 ErrorMessage,**不**调 NodeOperations
/// - 成功 → InfoMessage 含 entry.Package + version
/// - 失败 → ErrorMessage 含 result.Reason
///
/// 用 fake NodeOperations 子类(同 <see cref="CatalogViewModelTests.NoopNodeOps"/> 的 pattern),
/// 抓 DownloadAsync 调用的实参 —— 避免真跑 git。
/// </summary>
public sealed class CatalogViewModelDownloadTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _settingsRepoPath;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly FakeRefreshService _refreshService;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _projectRoot;

    public CatalogViewModelDownloadTests()
    {
        _db = new TestDb();
        _projectRoot = Path.Combine(
            Path.GetTempPath(), $"cat-vm-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, _projectRoot);
        _settingsRepoPath = Path.Combine(
            Path.GetTempPath(), $"cat-vm-dl-{Guid.NewGuid():N}.json");
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
    /// 抓 DownloadAsync 调用参数 + 强制返回可控 NodeOperationResult,避免真跑 git。
    /// </summary>
    private sealed class CapturingNodeOps : NodeOperations
    {
        public string? CalledLocalDir { get; private set; }
        public string? CalledNodeId { get; private set; }
        public string? CalledRepoUrl { get; private set; }
        public string? CalledTag { get; private set; }
        public int CallCount { get; private set; }
        public NodeOperationResult NextResult { get; set; } =
            NodeOperationResult.Ok("abc123");

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
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            CalledLocalDir = localDir;
            CalledNodeId = nodeId;
            CalledRepoUrl = repoUrl;
            CalledTag = targetTag;
            return Task.FromResult(NextResult);
        }
    }

    private CatalogViewModel NewVm(CapturingNodeOps ops) =>
        new(_catRepo, _versionRepo, ops, _refreshService, _settings, _settingsRepo, _projectRoot);

    /// <summary>
    /// 构造一个 catalog entry(raw_metadata 直含 string 值,避免 JSON 反序列化
    /// 把 string 转成 JsonElement 让 ExtractRepoUrl 的 <c>r is string</c> 失败)
    /// 然后 upsert 到 cache。Search 读回来的 RawMetadata 里 string 会被吃掉成 JsonElement,
    /// 所以测试后续要手动把 RawMetadata 还原成纯 string 字典再 assign。
    /// </summary>
    private CatalogEntry SeedEntry(string package, string repoUrl)
    {
        var entry = new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            CachedAt = "2026-07-13T00:00:00",
            ExpiresAt = "2027-07-13T00:00:00",
        };
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            entry.RawMetadata["repository"] = repoUrl;
        }
        _catRepo.Upsert(entry);
        return _catRepo.Search("", limit: 0).Find(e => e.Package == package)!;
    }

    /// <summary>
    /// 把 CatalogEntry.RawMetadata 里所有 <see cref="System.Text.Json.JsonElement"/>
    /// 还原成 string,CatalogViewModel.ExtractRepoUrl 的 <c>r is string</c> 才认。
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

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-dl-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            IProgress<ComfyUI.Manager.Models.CatalogEntry>? progress = null,
            IProgress<VersionFetchProgress>? versionProgress = null,
            IProgress<ComfyUI.Manager.Models.RateLimitInfo>? rateLimitProgress = null,
            IProgress<MetadataFetchProgress>? metadataProgress = null,
            IRateLimitState? rateLimitState = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(RefreshResult.Ok(0));
        }

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

    [Fact]
    public async Task DownloadCommand_TargetsLocalDir()
    {
        // local-nodes 默认值 → projectRoot/local-nodes
        var ops = new CapturingNodeOps();
        var vm = NewVm(ops);
        var entry = SeedEntry("pkg-dl-1", "https://example.com/pkg-dl-1.git");
        FixRawMetadataToStrings(entry);
        // 选一个 selected version,验证 tag 传参
        // v0.6.14: NodeVersionRepository.UpsertBatch 接 (source_url, package, VersionInfo)
        _versionRepo.UpsertBatch(new[] {
            (entry.SourceUrl, entry.Package, new VersionInfo { Tag = "v9.9.9", PublishedAt = "2026-01-01T00:00:00Z", IsPrerelease = false })
        });
        vm.Selected = entry;  // 触发 LoadVersionsForSelected
        Assert.NotNull(vm.SelectedVersion);

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(1, ops.CallCount);
        Assert.Equal(Path.Combine(_projectRoot, _settings.LocalNodeDirectory), ops.CalledLocalDir);
        Assert.Equal("pkg-dl-1", ops.CalledNodeId);
        Assert.Equal("https://example.com/pkg-dl-1.git", ops.CalledRepoUrl);
        Assert.Equal("v9.9.9", ops.CalledTag);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.InfoMessage);
        Assert.Contains("pkg-dl-1", vm.InfoMessage);
        // result.Version 是 NodeOperations 返的 HEAD sha(fake 返 "abc123"),
        // tag 是 caller 选的,不在 InfoMessage 文本里
        Assert.Contains("abc123", vm.InfoMessage ?? "");
    }

    [Fact]
    public async Task DownloadCommand_MissingRepoUrl_DoesNotCallNodeOps()
    {
        var ops = new CapturingNodeOps();
        var vm = NewVm(ops);
        // RawMetadata 没有 repository 也没有 url → ExtractRepoUrl 返 null
        var entry = SeedEntry("pkg-no-url", "");
        FixRawMetadataToStrings(entry);
        vm.Selected = entry;

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(0, ops.CallCount);
        Assert.Equal("catalog 条目缺 repository url", vm.ErrorMessage);
    }

    [Fact]
    public async Task DownloadCommand_EmptyLocalNodeDir_SetsErrorMessage()
    {
        var ops = new CapturingNodeOps();
        var vm = NewVm(ops);
        // 故意清空 → VM 应立即弹 ErrorMessage,不再调 NodeOperations
        _settings.LocalNodeDirectory = "";
        var entry = SeedEntry("pkg-empty-dir", "https://example.com/pkg.git");
        FixRawMetadataToStrings(entry);
        vm.Selected = entry;

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(0, ops.CallCount);
        Assert.Contains("本地节点目录为空", vm.ErrorMessage);
    }

    [Fact]
    public async Task DownloadCommand_FailureFromNodeOps_SetsErrorMessage()
    {
        var ops = new CapturingNodeOps { NextResult = NodeOperationResult.Fail("目录已存在:xxx") };
        var vm = NewVm(ops);
        var entry = SeedEntry("pkg-fail", "https://example.com/pkg-fail.git");
        FixRawMetadataToStrings(entry);
        vm.Selected = entry;

        vm.DownloadCommand.Execute(entry);
        await Task.Delay(50);

        Assert.Equal(1, ops.CallCount);
        Assert.Null(vm.InfoMessage);
        Assert.Contains("下载失败", vm.ErrorMessage);
        Assert.Contains("目录已存在", vm.ErrorMessage);
    }
}
