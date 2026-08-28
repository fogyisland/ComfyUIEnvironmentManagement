using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x: NodeStartupStatusViewModel 单元测试 — 走真 SQLite (TestDb) + 真 NodeRepository。
/// 覆盖:
/// - 空 env(ScannedNode 空)→ Nodes 空 + Summary "未扫描到任何节点"
/// - 混合成功/失败节点 → Summary "共 N 个节点,其中 M 个加载失败" + 排序(失败优先)
/// - 全部失败 → Summary "共 N 个节点,其中 N 个加载失败"
/// - 全部成功 → Summary "共 N 个节点,全部加载成功"
/// - CloseCommand → CloseRequested fire
/// </summary>
public class NodeStartupStatusViewModelTests
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private NodeRepository MakeRepo() => new NodeRepository(_db.Factory);

    private static ScannedNode MakeNode(string envId, string package, string? loadError = null)
    {
        var meta = new Dictionary<string, string>();
        if (loadError is not null)
        {
            meta["load_error"] = loadError;
        }
        return new ScannedNode
        {
            Id = $"{envId}::{package}",
            EnvId = envId,
            Package = package,
            PackagePath = $@"D:\Envs\{envId}\custom_nodes\{package}",
            ScanMeta = meta,
            Status = "enabled",
            Source = "env",
        };
    }

    [Fact]
    public void Ctor_EnvNeverStarted_NodesEmpty_SummaryMentionsEmpty()
    {
        var repo = MakeRepo();
        // 不 Upsert 任何 ScannedNode — env 从未启动,或 custom_nodes 为空

        var vm = new NodeStartupStatusViewModel(repo, "ghost-env", "ghost");

        Assert.Empty(vm.Nodes);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal(0, vm.FailedCount);
        Assert.Equal("ghost 的节点启动状态", vm.Title);
        Assert.Contains("未扫描到任何节点", vm.Summary);
    }

    [Fact]
    public void Ctor_AllSuccess_NodesPopulated_SummarySaysAllOk()
    {
        var repo = MakeRepo();
        repo.Upsert(MakeNode("env1", "ComfyUI-Manager"));
        repo.Upsert(MakeNode("env1", "ComfyUI_IPAdapter_plus"));
        repo.Upsert(MakeNode("env1", "rgthree-comfy"));

        var vm = new NodeStartupStatusViewModel(repo, "env1", "env1");

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(0, vm.FailedCount);
        Assert.Contains("全部加载成功", vm.Summary);
    }

    [Fact]
    public void Ctor_MixedSuccessAndFailure_SummaryCountsFailures_FailedFirst()
    {
        var repo = MakeRepo();
        repo.Upsert(MakeNode("env1", "ComfyUI-Manager"));
        repo.Upsert(MakeNode("env1", "ComfyUI_IPAdapter_plus"));
        repo.Upsert(MakeNode("env1", "rgthree-comfy", loadError: "ModuleNotFoundError: No module named 'torch'"));

        var vm = new NodeStartupStatusViewModel(repo, "env1", "env1");

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.Contains("共 3 个节点,其中 1 个加载失败", vm.Summary);

        // 失败节点排第一(用 HasLoadError 降序),其余按 package 字母升序
        Assert.True(vm.Nodes[0].HasLoadError);
        Assert.Equal("rgthree-comfy", vm.Nodes[0].Package);
        Assert.False(vm.Nodes[1].HasLoadError);
        Assert.False(vm.Nodes[2].HasLoadError);
    }

    [Fact]
    public void Ctor_AllFailed_SummarySaysAllFailed()
    {
        var repo = MakeRepo();
        repo.Upsert(MakeNode("env1", "a", loadError: "Failed to import module 'a'"));
        repo.Upsert(MakeNode("env1", "b", loadError: "ModuleNotFoundError: No module named 'b'"));

        var vm = new NodeStartupStatusViewModel(repo, "env1", "env1");

        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal(2, vm.FailedCount);
        Assert.Contains("共 2 个节点,其中 2 个加载失败", vm.Summary);
    }

    [Fact]
    public void Ctor_DifferentEnv_OnlyReturnsMatchingEnvNodes()
    {
        var repo = MakeRepo();
        repo.Upsert(MakeNode("env1", "alpha"));
        repo.Upsert(MakeNode("env1", "beta"));
        repo.Upsert(MakeNode("env2", "gamma", loadError: "Error loading gamma"));

        var vm = new NodeStartupStatusViewModel(repo, "env1", "env1");

        Assert.Equal(2, vm.Nodes.Count);
        Assert.All(vm.Nodes, n => Assert.Equal("env1", n.EnvId));
        Assert.DoesNotContain(vm.Nodes, n => n.Package == "gamma");
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        var repo = MakeRepo();
        repo.Upsert(MakeNode("env1", "alpha"));
        var vm = new NodeStartupStatusViewModel(repo, "env1", "env1");

        bool closed = false;
        vm.CloseRequested += () => closed = true;

        Assert.True(vm.CloseCommand.CanExecute(null));
        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }
}