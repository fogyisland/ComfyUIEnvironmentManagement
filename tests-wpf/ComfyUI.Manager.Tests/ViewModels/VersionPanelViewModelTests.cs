using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using System.Linq;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Tests.ViewModels;

public class VersionPanelViewModelTests
{
    [Fact]
    public async Task UpgradeCommand_DisabledWhenLocked()
    {
        var api = new FakeApiClient();
        api.Register("node/node-list", _ => new List<ScannedNode>
        {
            new() { Id = "sn-1", Package = "pkg-a" },
        });
        api.Register("node/list-versions", _ => new List<VersionStatus>
        {
            new() { Package = "pkg-a", HasUpdate = true, Locked = true },
        });
        var ws = new FakeWsClient();
        var vm = new VersionPanelViewModel(api, ws, "env-1");
        await Task.Delay(200);
        vm.SelectedPackage = "pkg-a";
        await Task.Delay(200);
        Assert.False(vm.UpgradeCommand.CanExecute(vm.Versions.FirstOrDefault()));
    }

    [Fact]
    public async Task WsVersionChanged_Reloads()
    {
        var api = new FakeApiClient();
        api.Register("node/node-list", _ => new List<ScannedNode>
        {
            new() { Id = "sn-1", Package = "pkg-a" },
        });
        api.Register("node/list-versions", _ => new List<VersionStatus>());
        var ws = new FakeWsClient();
        var vm = new VersionPanelViewModel(api, ws, "env-1");
        await Task.Delay(200);
        vm.SelectedPackage = "pkg-a";
        await Task.Delay(200);

        int nodeListCalls = 0;
        api.Register("node/node-list", _ =>
        {
            nodeListCalls++;
            return new List<ScannedNode>
            {
                new() { Id = "sn-1", Package = "pkg-a" },
            };
        });
        ws.Emit("versionChanged", "env-1", "pkg-a");
        await Task.Delay(200);
        Assert.True(nodeListCalls > 0);
    }
}