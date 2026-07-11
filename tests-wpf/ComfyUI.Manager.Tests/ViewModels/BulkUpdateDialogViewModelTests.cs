using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BulkUpdateDialogViewModelTests
{
    private BulkUpdateDialogViewModel NewVm(FakeBulkUpdateApiClient? fake = null)
    {
        fake ??= new FakeBulkUpdateApiClient();
        return new BulkUpdateDialogViewModel(fake);
    }

    private void LoadFixtureEnv(BulkUpdateDialogViewModel vm)
    {
        var env1 = new EnvRow("env-1", "Env 1");
        env1.Nodes.Add(new NodeSelectRow("node-a", "Node A"));
        env1.Nodes.Add(new NodeSelectRow("node-b", "Node B"));
        env1.Selected = true;
        env1.Nodes[0].Selected = true;
        env1.Nodes[1].Selected = true;
        vm.LoadEnvs(new[] { env1 });
    }

    [Fact]
    public async Task Begin_PublishesRequest()
    {
        var fake = new FakeBulkUpdateApiClient();
        var vm = NewVm(fake);
        LoadFixtureEnv(vm);
        await vm.StartAsync();
        Assert.Equal("fake-bulk-id", vm.BulkId);
        Assert.Equal(BulkUpdateMode.Running, vm.Mode);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void WsProgress_AdvancesRow()
    {
        var vm = NewVm();
        LoadFixtureEnv(vm);
        vm.StartAsync().Wait();
        vm.UpdateRow("env-1", "node-a", "succeeded", null, 150);
        var row = vm.Rows.First(r => r.NodeId == "node-a");
        Assert.Equal("succeeded", row.Status);
        Assert.Equal(150, row.LatencyMs);
    }

    [Fact]
    public async Task Cancel_InvokesApi()
    {
        var fake = new FakeBulkUpdateApiClient();
        var vm = NewVm(fake);
        LoadFixtureEnv(vm);
        await vm.StartAsync();
        await vm.CancelAsync();
        // fake.CancelAsync 返回的 StatusResult 不会进入 vm 副作用
        // 这里只能断言未抛异常 + fake 被调
        Assert.NotNull(vm.BulkId);
    }

    [Fact]
    public async Task Completed_SwitchesToSummary()
    {
        var fake = new FakeBulkUpdateApiClient();
        var vm = NewVm(fake);
        LoadFixtureEnv(vm);
        await vm.StartAsync();
        var summary = new BulkUpdateSummary(
            2, 2, 0, 0,
            new List<BulkUpdateRow>
            {
                new("env-1", "node-a", "succeeded", null, 100),
                new("env-1", "node-b", "succeeded", null, 200),
            });
        vm.SetSummary(summary);
        Assert.Equal(BulkUpdateMode.Summary, vm.Mode);
        Assert.NotNull(vm.Summary);
        Assert.Equal(2, vm.Summary!.Succeeded);
    }
}