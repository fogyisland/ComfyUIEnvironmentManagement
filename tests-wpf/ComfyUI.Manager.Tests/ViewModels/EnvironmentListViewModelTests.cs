using System.Windows.Threading;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelTests
{
    [Fact]
    public async Task Load_PopulatesEnvironmentsFromApi()
    {
        var api = new FakeApiClient();
        api.Register("env/list", _ => new List<Environment>
        {
            new() { Id = "env-1", Name = "env-1", Port = 8188, Status = "stopped" },
            new() { Id = "env-2", Name = "env-2", Port = 8189, Status = "running" },
        });
        var ws = new FakeWsClient();
        var vm = new EnvironmentListViewModel(api, ws);

        await Task.Delay(100);  // 等 _ = LoadAsync() 完成
        Assert.Equal(2, vm.Environments.Count);
        Assert.Equal("env-1", vm.Environments[0].Id);
    }

    [Fact]
    public async Task StartCommand_CallsStartEnvApi()
    {
        var api = new FakeApiClient();
        bool started = false;
        api.Register("process/start-env", req =>
        {
            started = true;
            return new { };
        });
        api.Register("env/list", _ => new List<Environment>
        {
            new() { Id = "env-1", Name = "env-1", Status = "stopped" },
        });
        var ws = new FakeWsClient();
        var vm = new EnvironmentListViewModel(api, ws);
        await Task.Delay(100);

        vm.Selected = vm.Environments[0];
        vm.StartCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(started);
    }

    [Fact]
    public async Task WsEvent_TriggersReload()
    {
        var api = new FakeApiClient();
        int callCount = 0;
        api.Register("env/list", _ =>
        {
            callCount++;
            return new List<Environment>
            {
                new() { Id = $"env-{callCount}", Status = "stopped" },
            };
        });
        var ws = new FakeWsClient();
        var vm = new EnvironmentListViewModel(api, ws);
        await Task.Delay(100);
        int initialCalls = callCount;

        ws.Emit("envListChanged");
        await Task.Delay(200);

        Assert.True(callCount > initialCalls);
    }
}