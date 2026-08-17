using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.18.2:<see cref="BulkUpdateViewModel"/> 现在管扁平 <see cref="UpdateItem"/>
/// checklist —— env-level(基础环境 + ComfyUI-Manager)跟 node-level(节点)统一
/// 表达,UI 单一列表,不再有 3 tab。
///
/// 删除了 v0.6.18.1 的 <see cref="BulkUpdateViewModel.AvailableNodes"/> /
/// 3 ICollectionView(被扁平的 UpdateItems 替代)。
/// </summary>
public class BulkUpdateViewModelTests
{
    private static BulkUpdateViewModel NewVmWithFixture(TestDb db)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var orch = new BulkUpdateOrchestrator(
            System.IO.Path.GetTempPath(), "git", envRepo, nodeRepo);

        var vm = new BulkUpdateViewModel(orch, nodeRepo);
        // v0.6.18.2:EnvRow.Selected 默认 true
        vm.LoadEnvs(new[] { new EnvRow("env-1", "Env 1") }, nodeRepo);
        return vm;
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

    private static void SeedNode(TestDb db, string id, string envId, string pkg, string packagePath)
    {
        using var conn = db.Factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scanned_nodes (id, env_id, package, package_path, status, source)
            VALUES (@id, @env, @pkg, @path, 'enabled', 'env');";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@env", envId);
        cmd.Parameters.AddWithValue("@pkg", pkg);
        cmd.Parameters.AddWithValue("@path", packagePath);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void LoadEnvs_PopulatesEnvRows()
    {
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        Assert.Single(vm.EnvRows);
    }

    [Fact]
    public void UpdateItems_DefaultHasTwoEnvLevelItemsPerEnv()
    {
        // 每个选中的 env 都贡献 2 条 env-level item(基础环境 + ComfyUI-Manager)
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);
        Assert.Equal(2, vm.UpdateItems.Count);
        Assert.Contains(vm.UpdateItems, i =>
            i.Target == BulkUpdateTargetKind.ComfyUi
            && i.DisplayName == "Env 1 · 基础环境"
            && i.NodeId == null);
        Assert.Contains(vm.UpdateItems, i =>
            i.Target == BulkUpdateTargetKind.ComfyUiManager
            && i.DisplayName == "Env 1 · ComfyUI-Manager"
            && i.NodeId == null);
    }

    [Fact]
    public void UpdateItems_AppendsNodeItemsAfterEnvLevel()
    {
        // env-level + node-level 混合顺序 —— env-level 在前,node-level 在后
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "my-node-a", "env-1", "pkg-a", "/tmp/env-1/custom_nodes/my-node-a");
        SeedNode(db, "my-node-b", "env-1", "pkg-b", "/tmp/env-1/custom_nodes/my-node-b");
        var vm = NewVmWithFixture(db);

        Assert.Equal(4, vm.UpdateItems.Count);
        // 前 2 条 env-level
        Assert.Equal(BulkUpdateTargetKind.ComfyUi, vm.UpdateItems[0].Target);
        Assert.Equal(BulkUpdateTargetKind.ComfyUiManager, vm.UpdateItems[1].Target);
        // 后 2 条 node-level
        Assert.Equal(BulkUpdateTargetKind.Node, vm.UpdateItems[2].Target);
        Assert.Equal(BulkUpdateTargetKind.Node, vm.UpdateItems[3].Target);
        Assert.All(vm.UpdateItems, i => Assert.True(i.Selected));
    }

    [Fact]
    public void UpdateItems_FiltersOutComfyUiManager()
    {
        // ComfyUI-Manager 是 env-level target,node 列表里跳过(避免重复显示)
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "ComfyUI-Manager", "env-1", "comfyui-manager",
            "/tmp/env-1/custom_nodes/ComfyUI-Manager");
        SeedNode(db, "real-node", "env-1", "real-pkg",
            "/tmp/env-1/custom_nodes/real-node");
        var vm = NewVmWithFixture(db);

        // env-level 2 条 + node-level 1 条(real-node) = 3 条
        Assert.Equal(3, vm.UpdateItems.Count);
        Assert.Single(vm.UpdateItems.Where(i => i.Target == BulkUpdateTargetKind.Node));
    }

    [Fact]
    public void UpdateItems_EnvUncheckedRemovesItsItems()
    {
        // 取消勾 env → 该 env 的 env-level + node-level items 全部从 UpdateItems 移除
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedEnv(db, "env-2", "Env 2");
        SeedNode(db, "node-on-1", "env-1", "pkg-1", "/tmp/1/node-on-1");
        SeedNode(db, "node-on-2", "env-2", "pkg-2", "/tmp/2/node-on-2");
        var vm = NewVmWithFixture(db);

        // NewVmWithFixture 默认只 env-1 → env-1 有 2 env-level + 1 node = 3 条
        Assert.Equal(3, vm.UpdateItems.Count);

        // 取消勾 env-1 → 没有 UpdateItems(env-2 没在 EnvRows 里)
        vm.EnvRows[0].Selected = false;
        Assert.Empty(vm.UpdateItems);
    }

    [Fact]
    public void UpdateItems_NodeUnchecked_LeavesStartEnabled()
    {
        // 取消勾某个 item(无论 env-level 还是 node-level)→ StartCommand 仍可执行
        // (只要至少有一个 item 勾上 + 至少有一个 env 勾上)。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);

        vm.UpdateItems[0].Selected = false;   // 取消基础环境
        Assert.True(vm.StartCommand.CanExecute(null));   // 还有 ComfyUI-Manager + 0 node
    }

    [Fact]
    public void UpdateItems_AllUnchecked_DisablesStart()
    {
        // 所有 item 都取消勾 → StartCommand disable
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "n1", "env-1", "p1", "/tmp/1/n1");
        var vm = NewVmWithFixture(db);

        foreach (var item in vm.UpdateItems) item.Selected = false;
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_EnabledWhenEnvSelected()
    {
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_DisabledWhenNoEnvsSelected()
    {
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        vm.EnvRows[0].Selected = false;
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleSelectAllEnvs_ClearsWhenAllSelected()
    {
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        vm.ToggleSelectAllEnvCommand.Execute(null);
        Assert.False(vm.EnvRows[0].Selected);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleSelectAllItems_FlipsSelection()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "n1", "env-1", "p1", "/tmp/1/n1");
        SeedNode(db, "n2", "env-1", "p2", "/tmp/1/n2");
        var vm = NewVmWithFixture(db);

        Assert.All(vm.UpdateItems, i => Assert.True(i.Selected));
        vm.ToggleSelectAllItemsCommand.Execute(null);
        Assert.All(vm.UpdateItems, i => Assert.False(i.Selected));
        vm.ToggleSelectAllItemsCommand.Execute(null);
        Assert.All(vm.UpdateItems, i => Assert.True(i.Selected));
    }

    [Fact]
    public void CancelCommand_DisabledWhenNotBusy()
    {
        var vm = NewVmWithFixture(new TestDb());
        Assert.False(vm.IsBusy);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Summary_InitiallyNull_NeverThrows()
    {
        var vm = NewVmWithFixture(new TestDb());
        Assert.Null(vm.Summary);
    }

    [Fact]
    public void LoadEnvs_ClearsPreviousList()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-a", "Env A");
        SeedEnv(db, "env-b", "Env B");
        var vm = NewVmWithFixture(db);
        var nodeRepo = new NodeRepository(db.Factory);
        vm.LoadEnvs(new[]
        {
            new EnvRow("env-a", "Env A"),
            new EnvRow("env-b", "Env B"),
        }, nodeRepo);
        Assert.Equal(2, vm.EnvRows.Count);
        Assert.Equal("env-a", vm.EnvRows[0].EnvId);
        // 2 env × 2 env-level = 4 条
        Assert.Equal(4, vm.UpdateItems.Count);
    }

    [Fact]
    public void UpdateItemRow_ItemName_ComputedFromTarget()
    {
        // BulkUpdateRow.ItemName 计算属性:Node → NodeId,ComfyUi → "Env · 基础环境"
        var nodeRow = new BulkUpdateRow("env-x", BulkUpdateTargetKind.Node, "pending", null, 0, 0, "my-pkg");
        Assert.Equal("my-pkg", nodeRow.ItemName);

        var baseEnvRow = new BulkUpdateRow("env-x", BulkUpdateTargetKind.ComfyUi, "pending", null, 0, 0, null);
        Assert.Equal("env-x · 基础环境", baseEnvRow.ItemName);

        var managerRow = new BulkUpdateRow("env-x", BulkUpdateTargetKind.ComfyUiManager, "pending", null, 0, 0, null);
        Assert.Equal("env-x · ComfyUI-Manager", managerRow.ItemName);
    }

    // ----- v0.6.18.2 G11+:HasRunningSelectedEnv -----

    [Fact]
    public void HasRunningSelectedEnv_FalseWhenAllStopped()
    {
        // 默认 EnvRow.Status="stopped" → 警告 banner 不显示
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);
        Assert.False(vm.HasRunningSelectedEnv);
    }

    [Fact]
    public void HasRunningSelectedEnv_TrueWhenSelectedEnvRunning()
    {
        // 选中 env 状态= running → 警告 banner 显示
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);
        vm.LoadEnvs(new[] { new EnvRow("env-1", "Env 1", status: "running") }, new NodeRepository(db.Factory));
        Assert.True(vm.HasRunningSelectedEnv);
    }

    [Fact]
    public void HasRunningSelectedEnv_FalseWhenRunningEnvUnchecked()
    {
        // env 在 running 但取消勾 → 不再"被选中",警告不显示
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);
        vm.LoadEnvs(new[] { new EnvRow("env-1", "Env 1", status: "running") }, new NodeRepository(db.Factory));
        Assert.True(vm.HasRunningSelectedEnv);
        vm.EnvRows[0].Selected = false;
        Assert.False(vm.HasRunningSelectedEnv);
    }

    [Fact]
    public void EnvRow_StoresStatusField()
    {
        // EnvRow.Status 必须从 Environment.Status 透传,running/stopped/failed 三态都保留
        var running = new EnvRow("e1", "E1", "running");
        Assert.Equal("running", running.Status);
        var stopped = new EnvRow("e2", "E2");
        Assert.Equal("stopped", stopped.Status);   // 默认值
        var failed = new EnvRow("e3", "E3", "failed");
        Assert.Equal("failed", failed.Status);
    }

    // ----- v0.6.18.4:ConsoleLog + IsConsoleVisible -----

    [Fact]
    public void ConsoleLog_InitiallyEmptyAndHidden()
    {
        // 初始无 log,IsBusy=false → Console 面板隐藏
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public void IsBusy_True_MakesConsoleVisible()
    {
        // IsBusy=true → IsConsoleVisible 自动 true(就算 log 还空)
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        vm.IsBusy = true;
        Assert.True(vm.IsConsoleVisible);
    }

    [Fact]
    public void IsBusy_False_KeepsConsoleVisibleWhenLogHasLines()
    {
        // run 完,IsBusy=false,但 log 还有行 → 面板保留可见(用户看完成报告)
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        vm.IsBusy = true;
        vm.ConsoleLog.Add("[env-1 · 基础环境] 开始:git pull");
        vm.IsBusy = false;
        Assert.True(vm.IsConsoleVisible);
    }

    [Fact]
    public void ClearConsoleLog_HidesConsole()
    {
        // 用户点 ✕ → ClearConsoleLog → IsConsoleVisible=false(即使 IsBusy 也保留)
        using var db = new TestDb();
        var vm = NewVmWithFixture(db);
        vm.IsBusy = true;
        vm.ConsoleLog.Add("line");
        Assert.True(vm.IsConsoleVisible);
        vm.ClearConsoleLog();
        Assert.False(vm.IsConsoleVisible);
        Assert.Empty(vm.ConsoleLog);
    }
}