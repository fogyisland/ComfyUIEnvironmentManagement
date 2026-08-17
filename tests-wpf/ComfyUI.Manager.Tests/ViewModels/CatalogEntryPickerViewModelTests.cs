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

    private NodeVersionRepository NewVersionRepo() =>
        new NodeVersionRepository(new CatalogCacheStore(_db.Path));

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
        FakeNodeOps? ops = null, string envId = "env-1",
        FakeRequirementsInstaller? reqInstaller = null)
    {
        var envRepo = new EnvironmentRepository(_db.Factory);
        var nodeRepo = NewNodeRepo();
        SeedEnv(envId);
        var fakeOps = ops ?? new FakeNodeOps(envRepo, nodeRepo, new Settings());
        var req = reqInstaller ?? new FakeRequirementsInstaller();
        return new CatalogEntryPickerViewModel(
            NewCatalogRepo(), nodeRepo, fakeOps, NewVersionRepo(), envRepo, req, envId);
    }

    /// <summary>
    /// FakeNodeOps:不真跑 git / 删目录,记录 UninstallAsync + InstallAsync 调用并返
    /// canned result。必须 override UninstallAsync(virtual)才能在 VM 调用时被派发到这里。
    /// 走真实 base ctor(envRepo/nodeRepo 必传,GitRunner="git" 测试机可能没有但不调,
    /// 不会真启动 git)。
    ///
    /// v0.6.14 T5:扩展 — override InstallAsync 记录 (envId, nodeId, repoUrl, targetTag)
    /// 调用 + 返 canned result。Success 路径会真 upsert ScannedNode row(envId, package),
    /// 这样 VM rebuild 后 ListByEnv 返 pkg-a,IsInstalled 变 true,test 验得出 rebuild。
    /// Fail 路径不动 db。
    /// </summary>
    private sealed class FakeNodeOps : NodeOperations
    {
        public List<(string EnvId, string NodeId)> UninstallCalls { get; } = new();
        public NodeOperationResult NextUninstallResult { get; set; } =
            NodeOperationResult.Ok(null);

        public List<(string EnvId, string NodeId, string RepoUrl, string? TargetTag,
            IReadOnlyList<PipRequirement>? CatalogPipReqs)> InstallCalls { get; } = new();
        public NodeOperationResult NextInstallResult { get; set; } =
            NodeOperationResult.Ok("fake-sha");

        private readonly NodeRepository _nodeRepo;
        private readonly EnvironmentRepository _envRepo;

        public FakeNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))))
        {
            _nodeRepo = nodeRepo;
            _envRepo = envRepo;
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

        public override Task<NodeOperationResult> InstallAsync(
            string envId, string nodeId, string repoUrl,
            string? targetTag = null,
            IReadOnlyList<PipRequirement>? catalogPipReqs = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            InstallCalls.Add((envId, nodeId, repoUrl, targetTag, catalogPipReqs));
            // Success → upsert ScannedNode row(envId, package)让 VM rebuild 后 IsInstalled=true
            if (NextInstallResult.Success)
            {
                var env = _envRepo.Get(envId);
                _nodeRepo.Upsert(new ScannedNode
                {
                    Id = nodeId,
                    EnvId = envId,
                    Package = nodeId,
                    PackagePath = $"/tmp/{envId}/custom_nodes/{nodeId}",
                    Version = NextInstallResult.Version ?? "fake-sha",
                    Status = "enabled",
                    ScanMeta = new Dictionary<string, string>(),
                });
            }
            return Task.FromResult(NextInstallResult);
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

    // ---- v0.6.14 T5:行内安装 InstallCommand ----

    [Fact]
    public async Task InstallCommand_CallsNodeOps_InstallAsync_WithCorrectArgs()
    {
        // Seed entry + raw_metadata["repository"] = "https://github.com/owner/repo"
        var catRepo = NewCatalogRepo();
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-install",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        };
        catRepo.Upsert(entry);
        // 没装 scanned_node row — IsInstalled 必 false

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings());
        var vm = NewVm(ops);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-install");
        Assert.False(item.IsInstalled);

        vm.InstallCommand.Execute(item);
        await WaitForCondition(() => ops.InstallCalls.Count == 1, timeoutMs: 2000);

        Assert.Single(ops.InstallCalls);
        var call = ops.InstallCalls[0];
        Assert.Equal("env-1", call.EnvId);
        Assert.Equal("pkg-install", call.NodeId);
        Assert.Equal("https://github.com/owner/repo", call.RepoUrl);
        Assert.Null(call.TargetTag);   // entry 没 seed versions → SelectedVersion 仍 null
    }

    [Fact]
    public async Task InstallCommand_Success_RefreshesItems_SetsInstalledBadge()
    {
        var catRepo = NewCatalogRepo();
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-install-ok",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        };
        catRepo.Upsert(entry);

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Ok("fake-sha"),
        };
        var vm = NewVm(ops);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-install-ok");
        vm.InstallCommand.Execute(item);

        // 等 rebuild 完成(InstallAsync → BuildItems)
        await WaitForCondition(
            () => vm.Items.Any(i => i.Entry.Package == "pkg-install-ok" && i.IsInstalled),
            timeoutMs: 2000);

        var after = vm.Items.Single(i => i.Entry.Package == "pkg-install-ok");
        Assert.True(after.IsInstalled);
        Assert.Equal("Installed", after.StatusKind);
        Assert.False(after.IsInstalling);   // rebuild 后旧 row 已被替换
    }

    [Fact]
    public async Task InstallCommand_Failure_ShowsError_KeepsRowNotInstalled()
    {
        var catRepo = NewCatalogRepo();
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-install-fail",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        };
        catRepo.Upsert(entry);

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Fail("git clone failed: 404"),
        };
        var vm = NewVm(ops);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-install-fail");
        vm.InstallCommand.Execute(item);

        // 等异步完成(InstallCalls 计数 + InstallError 写)
        await WaitForCondition(
            () => ops.InstallCalls.Count == 1 && vm.Items[0].InstallError is not null,
            timeoutMs: 2000);

        Assert.Single(ops.InstallCalls);
        // failure path 不 upsert ScannedNode row,IsInstalled 保持 false,IsInstalling 解除
        var after = vm.Items.Single(i => i.Entry.Package == "pkg-install-fail");
        Assert.False(after.IsInstalled);
        Assert.False(after.IsInstalling);
        Assert.Equal("git clone failed: 404", after.InstallError);
    }

    [Fact]
    public void InstallCommand_WhileAlreadyInstalling_IsDisabled()
    {
        SeedCatalogEntry("pkg-busy");

        var vm = NewVm();
        var item = vm.Items.Single(i => i.Entry.Package == "pkg-busy");

        Assert.True(vm.InstallCommand.CanExecute(item));

        // 模拟另一条并发正在装(设 IsInstalling=true)→ CanExecute 返 false
        item.IsInstalling = true;
        Assert.False(vm.InstallCommand.CanExecute(item));

        // 恢复:既没在装也没装过,CanExecute 重新为 true
        item.IsInstalling = false;
        Assert.True(vm.InstallCommand.CanExecute(item));
    }

    [Fact]
    public async Task InstallCommand_UsesSelectedVersion_AsTargetTag()
    {
        var catRepo = NewCatalogRepo();
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = SeedSourceUrl,
            Package = "pkg-versioned-install",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        };
        catRepo.Upsert(entry);
        // Seed 2 versions
        var versionRepo = new NodeVersionRepository(new CatalogCacheStore(_db.Path));
        versionRepo.UpsertBatch(new[]
        {
            (SeedSourceUrl, "pkg-versioned-install",
                new VersionInfo { Tag = "v1.0.0", PublishedAt = "2025-01-01T00:00:00Z", IsPrerelease = false }),
            (SeedSourceUrl, "pkg-versioned-install",
                new VersionInfo { Tag = "v2.0.0", PublishedAt = "2025-06-01T00:00:00Z", IsPrerelease = false }),
        });

        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings());
        var vm = NewVm(ops);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-versioned-install");
        Assert.Equal(2, item.Versions.Count);
        // 默认 SelectedVersion = LatestVersion(没设)/ 第一项 v1.0.0 → 改成 v2.0.0
        item.SelectedVersion = "v2.0.0";

        vm.InstallCommand.Execute(item);
        await WaitForCondition(() => ops.InstallCalls.Count == 1, timeoutMs: 2000);

        Assert.Single(ops.InstallCalls);
        Assert.Equal("v2.0.0", ops.InstallCalls[0].TargetTag);
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

    /// <summary>
    /// v0.6.15.6:fake RequirementsInstaller — override InstallNodeRequirementsAsync 让
    /// VM 测试不真跑 pip,只记录调用 + 返回可控结果。Default 返 Success(reason="节点无
    /// requirements.txt")= LocalNodeListViewModelTests 同款 skip 路径。
    /// </summary>
    private sealed class FakeRequirementsInstaller : RequirementsInstaller
    {
        public int InstallNodeReqCallCount { get; private set; }
        public Environment? LastEnv { get; private set; }
        public string? LastNodeDir { get; private set; }
        public RequirementsInstallResult NextResult { get; set; } =
            new(true, false, "节点无 requirements.txt", 0);

        public override Task<RequirementsInstallResult> InstallNodeRequirementsAsync(
            Environment env, string nodeDir,
            IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            InstallNodeReqCallCount++;
            LastEnv = env;
            LastNodeDir = nodeDir;
            return Task.FromResult(NextResult);
        }
    }

    // ---- v0.6.15.6:行内安装成功后自动装节点 requirements ----

    [Fact]
    public async Task InstallCommand_Success_TriggersNodeRequirementsInstall()
    {
        // catalog 装 pkg-reqs;FakeNodeOps.InstallAsync upsert ScannedNode(envId="env-1",
        // package="pkg-reqs");成功 → VM 调 RequirementsInstaller.InstallNodeRequirementsAsync。
        var catRepo = NewCatalogRepo();
        catRepo.Upsert(new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-reqs",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        });
        var reqInstaller = new FakeRequirementsInstaller();
        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Ok("fake-sha"),
        };
        var vm = NewVm(ops, reqInstaller: reqInstaller);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-reqs");
        Assert.False(item.IsInstalled);

        vm.InstallCommand.Execute(item);
        // 等 fire-and-forget 的 RunAsync 跑完
        await WaitForCondition(() => reqInstaller.InstallNodeReqCallCount == 1, timeoutMs: 2000);

        Assert.Equal(1, reqInstaller.InstallNodeReqCallCount);
        Assert.NotNull(reqInstaller.LastEnv);
        Assert.Equal("env-1", reqInstaller.LastEnv!.Id);
        Assert.NotNull(reqInstaller.LastNodeDir);
        Assert.Equal("pkg-reqs",
            Path.GetFileName(reqInstaller.LastNodeDir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.StartsWith("/tmp/env-1/custom_nodes", reqInstaller.LastNodeDir);
    }

    [Fact]
    public async Task InstallCommand_Success_NoRequirements_ShowsSkippedStatus()
    {
        // FakeRequirementsInstaller 默认 NextResult = Success(reason="节点无 requirements.txt")
        // → VM 应该设 NodeRequirementsStatus(面板 VM 不为 null,IsVisible 控制显示)。
        var catRepo = NewCatalogRepo();
        catRepo.Upsert(new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-noreqs",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        });
        var reqInstaller = new FakeRequirementsInstaller();
        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Ok("fake-sha"),
        };
        var vm = NewVm(ops, reqInstaller: reqInstaller);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-noreqs");
        vm.InstallCommand.Execute(item);

        await WaitForCondition(() => vm.NodeRequirementsStatus is not null, timeoutMs: 2000);
        Assert.Equal(1, reqInstaller.InstallNodeReqCallCount);
        // Panel VM 已挂上;IsVisible 控制实际显示。
        Assert.NotNull(vm.NodeRequirementsStatus);
    }

    [Fact]
    public async Task InstallCommand_Failure_DoesNotTriggerNodeRequirementsInstall()
    {
        // 失败 → InstallError 写原因 + IsInstalling=false,但不调 RequirementsInstaller。
        var catRepo = NewCatalogRepo();
        catRepo.Upsert(new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-fail",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        });
        var reqInstaller = new FakeRequirementsInstaller();
        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Fail("git clone failed"),
        };
        var vm = NewVm(ops, reqInstaller: reqInstaller);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-fail");
        vm.InstallCommand.Execute(item);

        await WaitForCondition(() => item.InstallError is not null, timeoutMs: 2000);

        Assert.Equal(0, reqInstaller.InstallNodeReqCallCount);
        Assert.Null(vm.NodeRequirementsStatus);
    }

    [Fact]
    public async Task InstallCommand_Success_PipFailure_KeepsRowInstalled_NoError()
    {
        // pip 失败 → row 已装(clone 成功)+ 不进 ErrorBanner(面板 VM 自己显示错误)。
        var catRepo = NewCatalogRepo();
        catRepo.Upsert(new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = "pkg-pipfail",
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/repo",
            },
            InstallType = "git",
        });
        var reqInstaller = new FakeRequirementsInstaller
        {
            NextResult = new RequirementsInstallResult(false, false, "pip 退出码 1", 0),
        };
        var ops = new FakeNodeOps(
            new EnvironmentRepository(_db.Factory), NewNodeRepo(), new Settings())
        {
            NextInstallResult = NodeOperationResult.Ok("fake-sha"),
        };
        var vm = NewVm(ops, reqInstaller: reqInstaller);

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-pipfail");
        vm.InstallCommand.Execute(item);
        await WaitForCondition(() => reqInstaller.InstallNodeReqCallCount == 1, timeoutMs: 2000);

        // DB row 已写入(pip 失败不回滚)
        var dbNode = NewNodeRepo().Get("pkg-pipfail");
        Assert.NotNull(dbNode);
        // 面板 VM 已挂上,用户能看到 pip 错误
        Assert.NotNull(vm.NodeRequirementsStatus);
        Assert.True(vm.NodeRequirementsStatus!.HasError);
        Assert.Contains("pip 退出码", vm.NodeRequirementsStatus!.Error);
    }

    // ---- v0.6.14 T3: Closed event ----

    [Fact]
    public void CancelCommand_FiresClosedEvent()
    {
        var vm = NewVm();
        bool closed = false;
        vm.Closed += () => closed = true;
        vm.CancelCommand.Execute(null);
        Assert.True(closed);
    }

    // ---- v0.6.14 T4: per-row version dropdown + LastUpdate ----

    private const string SeedSourceUrl = "https://example.com/catalog.json";

    /// <summary>
    /// Seed 一个 entry + 一次 catalog_version upsert。UpsertBatch 按 (source_url, package)
    /// 寻址,所以所有版本 upsert 都走相同的 sourceUrl + package。
    /// </summary>
    private void SeedVersions(string package, params (string tag, string published)[] versions)
    {
        var catRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        // 用 seed 跑过的 helper 写 catalog row(同 GUID = Entry.Id = node_id,后面 ListByNode 用)
        SeedCatalogEntry(package);
        var versionRepo = new NodeVersionRepository(new CatalogCacheStore(_db.Path));
        var items = versions.Select(v => (
            SourceUrl: SeedSourceUrl,
            Package: package,
            Version: new VersionInfo
            {
                Tag = v.tag,
                PublishedAt = v.published,
                IsPrerelease = false,
            })).ToArray();
        versionRepo.UpsertBatch(items);
    }

    [Fact]
    public void BuildItems_PopulatesVersionsFromNodeVersionRepo()
    {
        SeedVersions("pkg-versioned",
            ("v1.0.0", "2025-01-01T00:00:00Z"),
            ("v1.1.0", "2025-06-01T00:00:00Z"),
            ("v1.2.0", "2025-12-01T00:00:00Z"));

        var vm = NewVm();

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-versioned");
        Assert.Equal(3, item.Versions.Count);
        // ListByNode 已经按 published_at DESC 排序,VM 不再 reorder
        Assert.Equal("v1.2.0", item.Versions[0].Tag);
        Assert.Equal("v1.1.0", item.Versions[1].Tag);
        Assert.Equal("v1.0.0", item.Versions[2].Tag);
    }

    [Fact]
    public void BuildItems_SelectedVersion_DefaultsToLatestVersion_WhenInList()
    {
        // LatestVersion 命中 versions 里的某条 → 用 LatestVersion
        SeedVersions("pkg-a",
            ("v1.0.0", "2025-01-01T00:00:00Z"),
            ("v1.1.0", "2025-06-01T00:00:00Z"),
            ("v1.2.0", "2025-12-01T00:00:00Z"));
        // 覆盖 LatestVersion = "v1.1.0"(不是最新,但用户视角的 "latest" = GitHub metadata)
        var catRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        catRepo.UpdateLatestVersions(new[]
        {
            (SeedSourceUrl, "pkg-a", "v1.1.0"),
        });

        var vm = NewVm();

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-a");
        Assert.Equal("v1.1.0", item.SelectedVersion);
    }

    [Fact]
    public void BuildItems_SelectedVersion_FallsBackToFirstVersion_WhenLatestMissing()
    {
        // LatestVersion 不在 versions 里(可能 catalog fetch 早于 version metadata),
        // fallback 用列表第一项(已按 published_at DESC 排序,就是最新发布的)。
        SeedVersions("pkg-no-latest",
            ("v1.0.0", "2025-01-01T00:00:00Z"),
            ("v0.9.0", "2024-12-01T00:00:00Z"));
        // LatestVersion 写 "v9.9.9" — 不会命中 versions
        var catRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        catRepo.UpdateLatestVersions(new[]
        {
            (SeedSourceUrl, "pkg-no-latest", "v9.9.9"),
        });

        var vm = NewVm();

        var item = vm.Items.Single(i => i.Entry.Package == "pkg-no-latest");
        // 列表第一项 = v1.0.0(最新)
        Assert.Equal("v1.0.0", item.SelectedVersion);
    }
}