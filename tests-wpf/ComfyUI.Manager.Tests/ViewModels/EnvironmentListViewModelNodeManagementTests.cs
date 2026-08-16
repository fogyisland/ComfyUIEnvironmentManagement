using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15.8 T5:EnvironmentListViewModel 暴露 NodeManagement + UpgradeNodes
/// 底部面板命令 + per-env cache(切 env 不重建 VM,关闭面板保留 cache 让 re-open 复用)。
/// 6 测试覆盖:
/// (1) 新 env → 新 VM + 显示
/// (2) 同 env 重复开 → cache hit(同一 VM 实例)
/// (3) 切 env → 不同 VM 实例
/// (4) 关闭 → 隐藏 + cache 保留(re-open 同 env 复用)
/// (5) UpgradeNodes 同 (1)
/// (6) Busy env → OpenNodeManagementCommand.CanExecute = false(同其他 toggle 命令模式)
///
/// Brief 偏离(实际 ctor 参数顺序):
/// - 实际 EnvironmentListViewModel ctor 有 18 个参数(repo, launcher, envCreator,
///   baseEnvInstaller, settings, profileLoader, envDeleter, nodeOps, projectRoot,
///   requirementsInstaller, baseEnvUninstaller, requirementsUninstaller,
///   browserLauncher, errorBanner, comfyUiManagerInstaller, logger, catalogRepo,
///   nodeRepo, versionRepo)— brief 写的是 11 参错位 ctor(无 launcher / envCreator /
///   baseEnvInstaller / settings / profileLoader / envDeleter / projectRoot /
///   requirementsInstaller,直接用 gitRunner 等错配参数)。
/// - Ruling 4:必须 Read 实际 ctor 再写测试。
/// - 实际用法跟 ComfyUiManagerTests.MakeSut 一致:null! 占位跟 fakeInstaller 同样的
///   pattern,只是 T5 需要传 nodeOps + nodeRepo + catalogRepo 给命令用。
///
/// MarkEnvBusy / UnmarkEnvBusy:
/// - 实际都是 private。Brief 写的 _vm.MarkEnvBusy(env, "test") 编译不过。
/// - 用现有 internal void SetEnvBusyForTest(Environment env) test seam(打 ReqInstall
///   busy kind)— OpenNodeManagementCommand 的 CanExecute 只看 IsEnvBusy,busy kind
///   是哪个不区分。
/// - Unmark → 没有对应 test seam。简化方案:跑两个不同 env,第二个 env 没被 set busy
///   → CanExecute(env-b) = true(等同 UnmarkEnvBusy(env-a) 的语义)。
/// </summary>
public class EnvironmentListViewModelNodeManagementTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeNodeOperationsForManagement _nodeOps;
    private readonly CatalogRepository _catalogRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly string _tempRoot;
    private readonly EnvironmentListViewModel _vm;

    public EnvironmentListViewModelNodeManagementTests()
    {
        _nodeRepo = new NodeRepository(_db.Factory);
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeOps = new FakeNodeOperationsForManagement { NodeRepo = _nodeRepo };
        _catalogRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
        _versionRepo = new NodeVersionRepository(new CatalogCacheStore(_db.Path));

        // 工具栏 / per-row toggle 各自依赖 _projectRoot(创建/重载路径)+ launcher
        // (start/stop 入口)。这两个测试不触发,所以传 null! 走 short-circuit。
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistvm-nodemgmt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        SeedEnv("env-a", "env-A");
        SeedEnv("env-b", "env-B");

        // 实际 ctor 18 参;本测试不需要 launcher/envCreator/baseEnvInstaller/
        // settings/profileLoader/envDeleter/projectRoot/requirementsInstaller/
        // baseEnvUninstaller/requirementsUninstaller/browserLauncher/
        // comfyUiManagerInstaller/logger — 全部 null! 兜底,VM ctor 容忍。
        // 必传的:repo / nodeOps(_nodeOps) / nodeRepo(_nodeRepo) / catalogRepo +
        // versionRepo(OpenUpgradeNodes 内部要用,即使测试只验面板创建也得给真实值
        // 才能让 VM ctor 不被 nullable check 拦截)。
        _vm = new EnvironmentListViewModel(
            _envRepo,
            launcher: null!,
            envCreator: null!,
            baseEnvInstaller: null!,
            settings: null!,
            profileLoader: null!,
            envDeleter: null!,
            nodeOps: _nodeOps,
            projectRoot: _tempRoot,
            requirementsInstaller: null!,
            baseEnvUninstaller: null,
            requirementsUninstaller: null,
            browserLauncher: null,
            errorBanner: null,
            comfyUiManagerInstaller: null,
            logger: null,
            catalogRepo: _catalogRepo,
            nodeRepo: _nodeRepo,
            versionRepo: _versionRepo);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private void SeedEnv(string id, string name)
    {
        _envRepo.Upsert(new Environment
        {
            Id = id,
            Name = name,
            RootPath = $"/x/{id}",
            ComfyuiLayout = "standalone",
        });
    }

    private Environment GetEnv(string id)
        => _envRepo.Get(id) ?? throw new InvalidOperationException($"env {id} not found");

    [Fact]
    public void OpenNodeManagement_NewEnv_CreatesVM_ShowsPanel()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);

        Assert.NotNull(_vm.NodeManagement);
        Assert.True(_vm.IsNodeManagementVisible);
        Assert.Equal("env-A", _vm.NodeManagement!.EnvName);
    }

    [Fact]
    public void OpenNodeManagement_SameEnvTwice_ReusesCachedVM()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);
        var first = _vm.NodeManagement;

        _vm.OpenNodeManagementCommand.Execute(env);

        Assert.Same(first, _vm.NodeManagement);
    }

    [Fact]
    public void OpenNodeManagement_DifferentEnv_SwitchesPanelVM()
    {
        _vm.OpenNodeManagementCommand.Execute(GetEnv("env-a"));
        var first = _vm.NodeManagement;

        _vm.OpenNodeManagementCommand.Execute(GetEnv("env-b"));

        Assert.NotSame(first, _vm.NodeManagement);
        Assert.Equal("env-B", _vm.NodeManagement!.EnvName);
    }

    [Fact]
    public void CloseNodeManagementCommand_HidesPanel_PreservesCache()
    {
        var env = GetEnv("env-a");
        _vm.OpenNodeManagementCommand.Execute(env);
        var cached = _vm.NodeManagement;

        _vm.CloseNodeManagementCommand.Execute(null);

        Assert.Null(_vm.NodeManagement);
        Assert.False(_vm.IsNodeManagementVisible);

        // Re-open same env → reuse cached VM (same instance)
        _vm.OpenNodeManagementCommand.Execute(env);
        Assert.Same(cached, _vm.NodeManagement);
    }

    [Fact]
    public void OpenUpgradeNodes_NewEnv_CreatesVM_ShowsPanel()
    {
        var env = GetEnv("env-a");
        _vm.OpenUpgradeNodesCommand.Execute(env);

        Assert.NotNull(_vm.UpgradeNodes);
        Assert.True(_vm.IsUpgradeNodesVisible);
        Assert.Equal("env-A", _vm.UpgradeNodes!.EnvName);
    }

    [Fact]
    public void OpenNodeManagement_BusyEnv_GatedByCanExecute()
    {
        var env = GetEnv("env-a");
        // SetEnvBusyForTest sets ReqInstall busy kind; OpenNodeManagementCommand's
        // CanExecute only checks IsEnvBusy (not busy kind), so this suffices.
        _vm.SetEnvBusyForTest(env);
        Assert.False(_vm.OpenNodeManagementCommand.CanExecute(env));

        // No public UnmarkEnvBusy test seam — use a fresh env to verify non-busy
        // path (semantically equivalent: different env = different busy dict entry).
        var envB = GetEnv("env-b");
        Assert.True(_vm.OpenNodeManagementCommand.CanExecute(envB));
    }
}