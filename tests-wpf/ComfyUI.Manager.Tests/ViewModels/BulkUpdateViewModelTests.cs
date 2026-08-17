using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.18.1:<see cref="BulkUpdateViewModel"/> 现在管 env-level + node-level 双层
/// 状态(env-level target 自动跑,node-level 按 <see cref="NodeRow.Selected"/>
/// 决定)。
///
/// 删除了 v0.6.18 的 <c>UpdateComfyUi</c> / <c>UpdateComfyUiManager</c> /
/// <c>SelectedTargetKinds</c> 测试 —— 这些 API 已经从 UI 移除(target checkbox
/// 删了,改为 3 列 + TabControl 的"默认全跑 env-level,只勾选 node"语义)。
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
        // v0.6.18.1:EnvRow.Selected 默认 true,无需手动设
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
    public void AvailableNodes_EmptyWhenEnvHasNoNodes()
    {
        // env 没装任何 node → 中列 AvailableNodes 应该空
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        var vm = NewVmWithFixture(db);
        Assert.Empty(vm.AvailableNodes);
    }

    [Fact]
    public void AvailableNodes_PopulatedFromEnv()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "my-node-a", "env-1", "pkg-a", "/tmp/env-1/custom_nodes/my-node-a");
        SeedNode(db, "my-node-b", "env-1", "pkg-b", "/tmp/env-1/custom_nodes/my-node-b");
        var vm = NewVmWithFixture(db);

        Assert.Equal(2, vm.AvailableNodes.Count);
        Assert.Contains(vm.AvailableNodes, n => n.Id == "my-node-a");
        Assert.Contains(vm.AvailableNodes, n => n.Id == "my-node-b");
        // 默认勾上
        Assert.All(vm.AvailableNodes, n => Assert.True(n.Selected));
    }

    [Fact]
    public void AvailableNodes_FiltersOutComfyUiManager()
    {
        // v0.6.18.1:ComfyUI-Manager 是 env-level target(走 ComfyUiManager 槽位),
        // 不应该出现在 node checkbox 列表里(避免重复显示)。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "ComfyUI-Manager", "env-1", "comfyui-manager",
            "/tmp/env-1/custom_nodes/ComfyUI-Manager");
        SeedNode(db, "real-node", "env-1", "real-pkg",
            "/tmp/env-1/custom_nodes/real-node");
        var vm = NewVmWithFixture(db);

        Assert.Single(vm.AvailableNodes);
        Assert.Equal("real-node", vm.AvailableNodes[0].Id);
    }

    [Fact]
    public void AvailableNodes_EnvUncheckedHidesItsNodes()
    {
        // EnvRow.Selected = false → 它的节点不计入 AvailableNodes
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedEnv(db, "env-2", "Env 2");
        SeedNode(db, "node-on-1", "env-1", "pkg-1", "/tmp/1/node-on-1");
        SeedNode(db, "node-on-2", "env-2", "pkg-2", "/tmp/2/node-on-2");
        var vm = NewVmWithFixture(db);

        // NewVmWithFixture 默认选 env-1 → 只有 node-on-1
        Assert.Single(vm.AvailableNodes);
        Assert.Equal("node-on-1", vm.AvailableNodes[0].Id);

        // 取消勾 env-1 → 没有节点(因为 env-2 没在 EnvRows 里)
        vm.EnvRows[0].Selected = false;
        Assert.Empty(vm.AvailableNodes);
    }

    [Fact]
    public void AvailableNodes_NodeUnchecked_DisablesStartForNode()
    {
        // 取消勾 node 不影响 StartCommand.CanExecute(env-level target 还是跑),
        // 但 BuildJobs 会少一个 job —— 这里验 AvailableNodes.Selected 双向绑定。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "n1", "env-1", "p1", "/tmp/1/n1");
        var vm = NewVmWithFixture(db);

        vm.AvailableNodes[0].Selected = false;
        Assert.False(vm.AvailableNodes[0].Selected);
        Assert.True(vm.StartCommand.CanExecute(null));   // env-level 仍可跑
    }

    [Fact]
    public void StartCommand_EnabledWhenEnvSelected()
    {
        // v0.6.18.1:StartCommand 现在只看 EnvRows.Any(selected) —— env-level
        // target 自动跑(node-level 可由用户取消勾选)。不再依赖任何 target checkbox。
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
    public void ToggleSelectAllNodes_FlipsNodeSelection()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "n1", "env-1", "p1", "/tmp/1/n1");
        SeedNode(db, "n2", "env-1", "p2", "/tmp/1/n2");
        var vm = NewVmWithFixture(db);

        Assert.All(vm.AvailableNodes, n => Assert.True(n.Selected));
        vm.ToggleSelectAllNodesCommand.Execute(null);
        Assert.All(vm.AvailableNodes, n => Assert.False(n.Selected));
        vm.ToggleSelectAllNodesCommand.Execute(null);
        Assert.All(vm.AvailableNodes, n => Assert.True(n.Selected));
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
        Assert.Null(vm.Summary);   // inline 模式下 summary 没 run 过为 null
    }

    [Fact]
    public void LoadEnvs_ClearsPreviousList()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-a", "Env A");
        SeedEnv(db, "env-b", "Env B");
        var vm = NewVmWithFixture(db);
        // 重载
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        vm.LoadEnvs(new[]
        {
            new EnvRow("env-a", "Env A"),
            new EnvRow("env-b", "Env B"),
        }, nodeRepo);
        Assert.Equal(2, vm.EnvRows.Count);
        Assert.Equal("env-a", vm.EnvRows[0].EnvId);
    }

    [Fact]
    public void Rows_CollectionView_FilterByTargetKind()
    {
        // 验 3 个 ICollectionView 按 TargetKind 正确分流。
        using var db = new TestDb();
        SeedEnv(db, "env-1", "Env 1");
        SeedNode(db, "n1", "env-1", "p1", "/tmp/1/n1");
        var vm = NewVmWithFixture(db);

        vm.Rows.Add(new BulkUpdateRow("env-1", BulkUpdateTargetKind.ComfyUi, "pending", null, 0, 0, null));
        vm.Rows.Add(new BulkUpdateRow("env-1", BulkUpdateTargetKind.ComfyUiManager, "pending", null, 0, 0, null));
        vm.Rows.Add(new BulkUpdateRow("env-1", BulkUpdateTargetKind.Node, "pending", null, 0, 0, "n1"));

        Assert.Single(vm.BaseEnvRowsView.Cast<BulkUpdateRow>().ToList());
        Assert.Single(vm.ComfyUiManagerRowsView.Cast<BulkUpdateRow>().ToList());
        Assert.Single(vm.NodeRowsView.Cast<BulkUpdateRow>().ToList());
    }
}