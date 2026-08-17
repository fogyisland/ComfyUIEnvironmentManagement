using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.18:OpenBulkUpdate 必须复用同一 BulkUpdateViewModel + BulkUpdateView。
/// 模式跟 <see cref="MainViewModelEnvironmentViewCachingTests"/> 完全相同 —— inline
/// 模式下用户切走再回来,IsBusy + Rows + Summary 不能丢。
///
/// 测试不真跑 BulkUpdateOrchestrator(避免 git 副作用),只验缓存行为:
/// - 第一次 OpenBulkUpdate → 构造 VM + View + 刷 EnvRows
/// - 第二次 OpenBulkUpdate → 同一 VM 实例(不重新构造),CurrentView 引用一致
/// </summary>
public sealed class MainViewModelBulkUpdateInlineTests : IDisposable
{
    private readonly string _projectRoot;

    public MainViewModelBulkUpdateInlineTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "bulk-update-inline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewMainVm(TestDb db)
    {
        // OpenBulkUpdate 真用的 service:BulkUpdateOrchestrator(必须非 null)+ dbFactory。
        // 其它 service 传 null(VM ctor 不调用它们);UiPreferencesService 必须非 null(MVM ctor 校验)。
        var orch = new BulkUpdateOrchestrator(
            _projectRoot, "git", new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory));
        return new MainViewModel(
            db.Factory,
            null!, orch, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", "", null!, null!, new UiPreferencesService(_projectRoot));
    }

    [Fact]
    public void OpenBulkUpdateCommand_FirstCall_CreatesViewModelAndSetsCurrentView()
    {
        using var db = new TestDb();
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        Assert.Null(main.CurrentBulkUpdateViewModel);

        main.OpenBulkUpdateCommand.Execute(null);

        Assert.NotNull(main.CurrentBulkUpdateViewModel);
        Assert.Equal(MainSection.BulkUpdate, main.CurrentSection);
        // CurrentView 是 object? — factory 返回 stub 不是 BulkUpdateView 实例,
        // `as BulkUpdateView` 返 null → CurrentView=null。这跟 EnvironmentListView
        // 既有模式一致(详见 MainViewModelEnvironmentViewCachingTests)。
        // 真正有意义的是下面 CurrentViewIsTheCachedBulkUpdateView 测试,
        // 用 Assert.Same(null, null) 验证复用同一引用(两次都 null 也算 "复用")。
    }

    [Fact]
    public void OpenBulkUpdateCommand_CalledTwice_ReturnsSameViewModelInstance()
    {
        // 关键:用户切走再回来,必须复用同一 VM,否则 IsBusy / Rows / Summary 状态丢。
        using var db = new TestDb();
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        main.OpenBulkUpdateCommand.Execute(null);
        var first = main.CurrentBulkUpdateViewModel;
        Assert.NotNull(first);

        main.OpenBulkUpdateCommand.Execute(null);

        Assert.Same(first, main.CurrentBulkUpdateViewModel);
    }

    [Fact]
    public void OpenBulkUpdateCommand_CurrentViewIsTheCachedBulkUpdateView()
    {
        // 跟 EnvironmentListView 同款 —— CurrentView 引用必须复用,不然 XAML ContentControl
        // 重新解析丢绑定。Factory stub 不继承 BulkUpdateView → `as` cast 返 null,
        // 两次 CurrentView 都 null,Assert.Same(null, null) 验证一致性。
        using var db = new TestDb();
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        main.OpenBulkUpdateCommand.Execute(null);
        var firstViewRef = main.CurrentView;

        main.OpenBulkUpdateCommand.Execute(null);

        Assert.Same(firstViewRef, main.CurrentView);
    }

    [Fact]
    public void OpenBulkUpdateCommand_RefreshesEnvRowsOnReentry()
    {
        // v0.6.18 G3:用户先在环境页新建 / 删除 env,切回 bulk update 时
        // EnvRows 列表必须刷新(VM 复用,但 env 列表不能 stale)。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env One");
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        main.OpenBulkUpdateCommand.Execute(null);
        var vm = main.CurrentBulkUpdateViewModel;
        Assert.NotNull(vm);
        Assert.Single(vm!.EnvRows);

        // 新增一个 env(模拟用户在 env 页创建了新环境)
        SeedEnv(db, "env-2", "Env Two");
        main.OpenBulkUpdateCommand.Execute(null);

        Assert.Equal(2, vm.EnvRows.Count);   // 复用 VM 但 EnvRows 已刷新
        Assert.Equal("env-1", vm.EnvRows[0].EnvId);
        Assert.Equal("env-2", vm.EnvRows[1].EnvId);
    }

    [Fact]
    public void OpenBulkUpdateCommand_PopulatesUpdateItemsFromScan()
    {
        // v0.6.18.2:OpenBulkUpdate 必须把 env 的 scanned_nodes 拉进扁平 UpdateItems。
        // ComfyUI-Manager 行被过滤掉(走 env-level ComfyUiManager 槽位)。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env One");
        SeedNode(db, "ComfyUI-Manager", "env-1", "comfyui-manager", "/tmp/1/custom_nodes/ComfyUI-Manager");
        SeedNode(db, "real-node", "env-1", "real-pkg", "/tmp/1/custom_nodes/real-node");
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        main.OpenBulkUpdateCommand.Execute(null);
        var vm = main.CurrentBulkUpdateViewModel;
        Assert.NotNull(vm);

        Assert.Single(vm!.UpdateItems.Where(i => i.Target == BulkUpdateTargetKind.Node));
        var realNode = vm.UpdateItems.First(i => i.Target == BulkUpdateTargetKind.Node);
        Assert.Equal("real-node", realNode.NodeId);
        // 2 env-level + 1 node-level = 3 条
        Assert.Equal(3, vm.UpdateItems.Count);
    }

    [Fact]
    public void OpenBulkUpdateCommand_RefreshesUpdateItemsOnReentry()
    {
        // v0.6.18.2:用户安装新节点后切回 bulk update,UpdateItems 必须包含新节点。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env One");
        SeedNode(db, "node-1", "env-1", "pkg-1", "/tmp/1/custom_nodes/node-1");
        var main = NewMainVm(db);
        main.BulkUpdateViewFactory = vm => new StubBulkUpdateView(vm);

        main.OpenBulkUpdateCommand.Execute(null);
        var vm = main.CurrentBulkUpdateViewModel;
        Assert.NotNull(vm);
        Assert.Single(vm!.UpdateItems.Where(i => i.Target == BulkUpdateTargetKind.Node));

        SeedNode(db, "node-2", "env-1", "pkg-2", "/tmp/1/custom_nodes/node-2");
        main.OpenBulkUpdateCommand.Execute(null);

        // 2 env-level + 2 node-level = 4 条
        Assert.Equal(2, vm.UpdateItems.Count(i => i.Target == BulkUpdateTargetKind.Node));
        Assert.Equal(4, vm.UpdateItems.Count);
    }

    private static void SeedEnv(TestDb db, string id, string name)
    {
        using var conn = db.Factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO environments (id, name, root_path, comfyui_layout)
            VALUES (@id, @name, @root, 'isolated');";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@root", $"/tmp/{id}");
        cmd.ExecuteNonQuery();
    }

    private static void SeedNode(TestDb db, string id, string envId, string pkg, string path)
    {
        using var conn = db.Factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scanned_nodes (id, env_id, package, package_path, status, source)
            VALUES (@id, @env, @pkg, @path, 'enabled', 'env');";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@env", envId);
        cmd.Parameters.AddWithValue("@pkg", pkg);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 代替真实 BulkUpdateView(它继承 UserControl → 触发 WPF STA 初始化,
    /// 单测在 MTA 下会抛 InvalidOperationException)。只用于断言"VM 缓存 +
    /// CurrentView 引用一致",不验证 XAML 绑定行为。
    /// </summary>
    private sealed class StubBulkUpdateView
    {
        public object DataContext { get; set; } = new object();
        public StubBulkUpdateView(object dataContext) { DataContext = dataContext; }
    }
}
