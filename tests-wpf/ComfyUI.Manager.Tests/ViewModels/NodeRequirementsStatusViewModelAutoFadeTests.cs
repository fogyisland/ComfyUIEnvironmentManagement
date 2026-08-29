using System;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class NodeRequirementsStatusViewModelAutoFadeTests
{
    private sealed class FakeInstaller : RequirementsInstaller
    {
        public RequirementsInstallResult Result { get; set; }
            = new RequirementsInstallResult(true, false, null, 0);

        public override Task<RequirementsInstallResult> InstallNodeRequirementsAsync(
            Environment env, string nodeDir,
            IProgress<string>? progress, CancellationToken ct)
            => Task.FromResult(Result);
    }

    [Fact]
    public async Task RunAsync_Success_HidesAfter2Seconds()
    {
        var env = new Environment { Id = "e1", Name = "test-env" };
        var installer = new FakeInstaller
        {
            Result = new RequirementsInstallResult(
                Success: true, Cancelled: false,
                Reason: "节点无 requirements.txt", InstalledCount: 0)
        };
        var vm = new NodeRequirementsStatusViewModel(env, "node1", "C:/fake", installer);

        // 把 fade delay 调成 50ms 加速测试。override factory 静态字段要公开。
        vm.FadeDelaySuccessMs = 50;
        vm.FadeDelayFailureMs = 50;

        await vm.RunAsync();
        Assert.True(vm.IsVisible);            // 刚跑完还在
        // v1.0.0.x #724 fix:同 RunAsync_Failure_HidesAfter5Seconds,polling 等
        // IsVisible=false,避免 Task.Delay 时序 race。
        for (int i = 0; i < 100 && vm.IsVisible; i++)
        {
            await Task.Delay(20);
        }
        Assert.False(vm.IsVisible);           // 2s (测试用 50ms) 后 Hide
    }

    [Fact]
    public async Task RunAsync_Failure_HidesAfter5Seconds()
    {
        var env = new Environment { Id = "e1", Name = "test-env" };
        var installer = new FakeInstaller
        {
            Result = new RequirementsInstallResult(
                Success: false, Cancelled: false,
                Reason: "pip 退出码 1", InstalledCount: 0)
        };
        var vm = new NodeRequirementsStatusViewModel(env, "node1", "C:/fake", installer)
        {
            FadeDelayFailureMs = 50,
            FadeDelaySuccessMs = 50,
        };

        await vm.RunAsync();
        Assert.True(vm.HasError);
        // v1.0.0.x #724 fix:full-suite 跑时 Task.Delay(200) 在线程池繁忙时
        // 不一定比 AutoHideAsync 内的 Task.Delay(50) 先 fire → IsVisible 还没
        // 变 false。改 polling 等最多 2 秒。
        for (int i = 0; i < 100 && vm.IsVisible; i++)
        {
            await Task.Delay(20);
        }
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Hide_CancelsAutoFadeTimer()
    {
        var env = new Environment { Id = "e1", Name = "test-env" };
        var installer = new FakeInstaller();
        var vm = new NodeRequirementsStatusViewModel(env, "node1", "C:/fake", installer)
        {
            FadeDelaySuccessMs = 100,
        };

        // 起 RunAsync 后立刻 Hide()
        var runTask = vm.RunAsync();
        vm.Hide();                              // 用户手关
        Assert.False(vm.IsVisible);

        // 100ms 后 _hideCts 已被取消,Hide() 不会重复触发,IsVisible 保持 false
        await Task.Delay(200);
        Assert.False(vm.IsVisible);

        await runTask;                          // 清掉 fire-and-forget task
    }
}