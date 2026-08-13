using System;
using System.Collections.Generic;
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
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.14 picker redesign:env-aware catalog picker 单元测试。
/// 覆盖 join / query / filter / OK gate / uninstall 重置。
/// FakeNodeOps 走 NodeOperations 子类 override UninstallAsync,不动真 git。
/// </summary>
public sealed class CatalogEntryPickerViewModelTests : IDisposable
{
    private readonly TestDb _db = new();

    public CatalogEntryPickerViewModelTests()
    {
    }

    public void Dispose() => _db.Dispose();

    private CatalogRepository NewCatalogRepo() =>
        new CatalogRepository(new CatalogCacheStore(_db.Path));

    private NodeRepository NewNodeRepo() => new NodeRepository(_db.Factory);

    /// <summary>
    /// Seed 一个 env(必填 fields,NodeOperations.UninstallAsync 路径会查 env)。
    /// </summary>
    private Environment SeedEnv(string id)
    {
        var envRepo = new EnvironmentRepository(_db.Factory);
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"/tmp/{id}",
            ComfyuiLayout = "isolated",
            CustomNodesPath = $"/tmp/{id}/custom_nodes",
            Port = 8188,
            Status = "stopped",
        };
        envRepo.Upsert(env);
        return env;
    }

    private void SeedCatalogEntry(string package, string? latestVersion = null,
        string? author = null, string? description = null)
    {
        var rawMeta = new Dictionary<string, object?>();
        if (author is not null) rawMeta["author"] = author;
        if (description is not null) rawMeta["description"] = description;
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = package,
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = rawMeta,
            Author = author,
            Description = description,
            InstallType = "git",
        };
        var repo = NewCatalogRepo();
        repo.Upsert(entry);
        // CatalogRepository.Upsert 不写 latest_version 列(只写 typed columns + 11 GitHub metadata)。
        // latest_version 由 GitHubVersionService 跑 UpdateLatestVersions 单独写入。
        // seed 阶段手动调一次模拟它被填好。
        if (latestVersion is not null)
        {
            repo.UpdateLatestVersions(new[] { (entry.SourceUrl, entry.Package, latestVersion) });
        }
    }

    private void SeedScannedNode(string envId, string package, string? version = null,
        string? installedTag = null)
    {
        var scanMeta = new Dictionary<string, string>();
        if (installedTag is not null) scanMeta["installed_tag"] = installedTag;
        NewNodeRepo().Upsert(new ScannedNode
        {
            Id = package,
            EnvId = envId,
            Package = package,
            PackagePath = $"/tmp/{envId}/custom_nodes/{package}",
            Version = version,
            ScanMeta = scanMeta,
        });
    }

    private CatalogEntryPickerViewModel NewVm(
        FakeNodeOps? ops = null, string envId = "env-1")
    {
        var envRepo = new EnvironmentRepository(_db.Factory);
        var nodeRepo = NewNodeRepo();
        SeedEnv(envId);
        var fakeOps = ops ?? new FakeNodeOps(envRepo, nodeRepo, new Settings());
        return new CatalogEntryPickerViewModel(
            NewCatalogRepo(), nodeRepo, fakeOps, envId);
    }

    /// <summary>
    /// FakeNodeOps:不真跑 git / 删目录,记录 UninstallAsync 调用并返 canned result。
    /// 必须 override UninstallAsync(virtual)才能在 VM 调用时被派发到这里。
    /// 走真实 base ctor(envRepo/nodeRepo 必传,GitRunner="git" 测试机可能没有但不调,
    /// 不会真启动 git)。
    ///
    /// Success 路径会真删 scanned_nodes row(nodeRepo.Delete)— 否则 VM rebuild 后
    /// ListByEnv 仍返回 pkg-a,IsInstalled 不变,test 验不出 rebuild 行为。
    /// Fail 路径不动 db。
    /// </summary>
    private sealed class FakeNodeOps : NodeOperations
    {
        public List<(string EnvId, string NodeId)> UninstallCalls { get; } = new();
        public NodeOperationResult NextUninstallResult { get; set; } =
            NodeOperationResult.Ok(null);

        private readonly NodeRepository _nodeRepo;

        public FakeNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))))
        {
            _nodeRepo = nodeRepo;
        }

        public override Task<NodeOperationResult> UninstallAsync(
            string envId, string nodeId, CancellationToken ct = default)
        {
            UninstallCalls.Add((envId, nodeId));
            // Success → 删 row 让 VM rebuild 后 IsInstalled 变 false
            if (NextUninstallResult.Success)
            {
                _nodeRepo.Delete(nodeId);
            }
            return Task.FromResult(NextUninstallResult);
        }
    }

    // ---- Join 行为 ----

    [Fact]
    public void Constructor_JoinsCatalogWithInstalledByPackage()
    {
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");
        SeedCatalogEntry("pkg-b", latestVersion: "2.0.0");
        SeedScannedNode("env-1", "pkg-a", version: "abc12345", installedTag: "0.9.0");

        var vm = NewVm();

        Assert.Equal(2, vm.Items.Count);
        var pkgA = vm.Items.Single(i => i.Entry.Package == "pkg-a");
        Assert.True(pkgA.IsInstalled);
        Assert.True(pkgA.IsOutdated);  // tag 0.9.0 vs latest 1.0.0
        Assert.Equal("0.9.0", pkgA.InstalledTag);
        Assert.Equal("已过时", pkgA.StatusBadge);

        var pkgB = vm.Items.Single(i => i.Entry.Package == "pkg-b");
        Assert.False(pkgB.IsInstalled);
        Assert.False(pkgB.IsOutdated);
        Assert.Equal("未安装", pkgB.StatusBadge);
    }

    [Fact]
    public void Constructor_InstalledTagMissing_DoesNotClaimOutdated()
    {
        // node row 有但 scanMeta 没 installed_tag(老节点)— 不该判 outdated;
        // InstalledVersionDisplay 走 fallback sha 前 8 字符。
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");
        SeedScannedNode("env-1", "pkg-a", version: "abcdef0123456789", installedTag: null);

        var vm = NewVm();

        var item = vm.Items.Single();
        Assert.True(item.IsInstalled);
        Assert.False(item.IsOutdated);
        Assert.Equal("已安装", item.StatusBadge);
        Assert.Equal("abcdef01", item.InstalledVersionDisplay);
    }

    [Fact]
    public void Constructor_InstalledNoLatestVersion_DoesNotClaimOutdated()
    {
        SeedCatalogEntry("pkg-a", latestVersion: null);
        SeedScannedNode("env-1", "pkg-a", version: "abc12345", installedTag: "0.9.0");

        var vm = NewVm();

        var item = vm.Items.Single();
        Assert.True(item.IsInstalled);
        Assert.False(item.IsOutdated);
        Assert.Equal("已安装", item.StatusBadge);
        // InstalledVersionDisplay 走 InstalledTag fallback
        Assert.Equal("0.9.0", item.InstalledVersionDisplay);
    }

    // ---- Query 行为 ----

    [Fact]
    public void Query_EmptyReturnsAll()
    {
        SeedCatalogEntry("pkg-a");
        SeedCatalogEntry("pkg-b");
        SeedCatalogEntry("pkg-c");

        var vm = NewVm();

        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public void Query_TextFiltersByPackageOrDescription()
    {
        SeedCatalogEntry("controlnet", description: "image control");
        SeedCatalogEntry("ipadapter", description: "ip adapter plus");
        SeedCatalogEntry("impact", description: "misc control helpers");

        var vm = NewVm();
        vm.Query = "control";

        // 包名 hit 1 + 描述 hit 1 = 2
        Assert.Equal(2, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Entry.Package == "controlnet");
        Assert.Contains(vm.Items, i => i.Entry.Package == "impact");
    }

    // ---- Filter chip 行为 ----

    [Fact]
    public void Filter_NotInstalled_HidesInstalled()
    {
        SeedCatalogEntry("pkg-a");
        SeedCatalogEntry("pkg-b");
        SeedScannedNode("env-1", "pkg-a", installedTag: "1.0.0");

        var vm = NewVm();
        vm.ActiveFilter = PickerFilter.NotInstalled;

        Assert.Single(vm.Items);
        Assert.Equal("pkg-b", vm.Items[0].Entry.Package);
    }

    [Fact]
    public void Filter_Installed_HidesNotInstalled()
    {
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");  // installed, up-to-date
        SeedCatalogEntry("pkg-b");                            // not installed
        SeedScannedNode("env-1", "pkg-a", installedTag: "1.0.0");

        var vm = NewVm();
        vm.ActiveFilter = PickerFilter.Installed;

        Assert.Single(vm.Items);
        Assert.Equal("pkg-a", vm.Items[0].Entry.Package);
    }

    [Fact]
    public void Filter_Outdated_ShowsOnlyInstalledWithDifferentTag()
    {
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");
        SeedCatalogEntry("pkg-b", latestVersion: "2.0.0");
        SeedScannedNode("env-1", "pkg-a", installedTag: "0.9.0");
        SeedScannedNode("env-1", "pkg-b", installedTag: "2.0.0");  // same → not outdated

        var vm = NewVm();
        vm.ActiveFilter = PickerFilter.Outdated;

        Assert.Single(vm.Items);
        Assert.Equal("pkg-a", vm.Items[0].Entry.Package);
    }

    [Fact]
    public void Filter_AndQuery_Intersect()
    {
        SeedCatalogEntry("controlnet", latestVersion: "1.0.0");
        SeedCatalogEntry("ipadapter", latestVersion: "2.0.0");
        SeedScannedNode("env-1", "controlnet", installedTag: "1.0.0");  // not outdated
        SeedScannedNode("env-1", "ipadapter", installedTag: "1.0.0");  // outdated

        var vm = NewVm();
        vm.ActiveFilter = PickerFilter.NotInstalled;
        vm.Query = "adapter";

        // ipadapter 描述/包名 hit "adapter",但 installed → 排除
        Assert.Empty(vm.Items);
    }

    // ---- Command 行为 ----

    [Fact]
    public void OkCommand_FiresCloseWithEntry_OnlyForNotInstalled()
    {
        SeedCatalogEntry("pkg-a");
        SeedScannedNode("env-1", "pkg-a", installedTag: "1.0.0");

        var vm = NewVm();

        // installed → CanExecute false
        vm.Selected = vm.Items[0];
        Assert.False(vm.OkCommand.CanExecute(null));

        // 选 not-installed:seed 第二个
        SeedCatalogEntry("pkg-b");
        vm = NewVm();
        // 找到 not-installed 那条
        var notInstalled = vm.Items.Single(i => !i.IsInstalled);
        CatalogEntry? firedEntry = null;
        vm.CloseWithEntry += e => firedEntry = e;
        vm.Selected = notInstalled;
        Assert.True(vm.OkCommand.CanExecute(null));
        vm.OkCommand.Execute(null);
        Assert.NotNull(firedEntry);
        Assert.Equal("pkg-b", firedEntry!.Package);
    }

    [Fact]
    public async Task UninstallCommand_CallsNodeOps_AndRefreshesItems()
    {
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");
        SeedScannedNode("env-1", "pkg-a", installedTag: "0.9.0");

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextUninstallResult = NodeOperationResult.Ok(null),
        };
        var vm = NewVm(ops);

        var installed = vm.Items.Single(i => i.IsInstalled);
        Assert.True(vm.UninstallCommand.CanExecute(installed));

        vm.UninstallCommand.Execute(installed);
        // 等异步完成
        await WaitForCondition(() => ops.UninstallCalls.Count == 1, timeoutMs: 2000);

        Assert.Single(ops.UninstallCalls);
        Assert.Equal(("env-1", "pkg-a"), ops.UninstallCalls[0]);
        // rebuild 后 IsInstalled=false
        var after = vm.Items.Single(i => i.Entry.Package == "pkg-a");
        Assert.False(after.IsInstalled);
    }

    [Fact]
    public async Task UninstallCommand_FailedResult_LeavesItemsIntact()
    {
        SeedCatalogEntry("pkg-a", latestVersion: "1.0.0");
        SeedScannedNode("env-1", "pkg-a", installedTag: "0.9.0");

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextUninstallResult = NodeOperationResult.Fail("test failure"),
        };
        var vm = NewVm(ops);

        var installed = vm.Items.Single(i => i.IsInstalled);
        vm.UninstallCommand.Execute(installed);

        await WaitForCondition(() => ops.UninstallCalls.Count == 1, timeoutMs: 2000);

        Assert.Single(ops.UninstallCalls);
        // failed → rebuild 没触发 → items unchanged
        var after = vm.Items.Single(i => i.Entry.Package == "pkg-a");
        Assert.True(after.IsInstalled);
    }

    private static async Task WaitForCondition(Func<bool> predicate, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"condition not met within {timeoutMs}ms");
            await Task.Delay(20);
        }
    }
}