using System;
using System.Threading;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class SplashViewModelTests
{
    private static SplashViewModel MakeVm(Action? onTick = null)
    {
        var vm = new SplashViewModel("ComfyUI Manager", "test tagline", "v0.6.8");
        if (onTick is not null)
        {
            // fake timer:每次 Start 给一个 IDisposable,tick 触发调 onTick 一次
            vm.TimerFactory = (callback, _) =>
            {
                onTick();
                return new NoopDisposable();
            };
        }
        return vm;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void Ctor_TitleTaglineVersion_AreSet()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        Assert.Equal("T", vm.Title);
        Assert.Equal("sub", vm.Tagline);
        Assert.Equal("v1.0", vm.Version);
        Assert.False(vm.IsFading);
    }

    [Fact]
    public void NotifyMainWindowReady_StartsTimer()
    {
        var ticks = 0;
        var vm = MakeVm(onTick: () => ticks++);

        vm.NotifyMainWindowReady();

        // fake timer 立即 fire 一次
        Assert.Equal(1, ticks);
        Assert.False(vm.IsFading);  // 第一次 Tick,elapsed 极小 → 不 fade
    }

    [Fact]
    public void NotifyMainWindowReady_BeforeMinDisplayTime_TimerDoesNotFade()
    {
        var elapsed = TimeSpan.Zero;
        var vm = MakeVm(onTick: null);
        vm.TimerFactory = (callback, interval) =>
        {
            // fake:模拟"elapsed 还没到 3s"→ 不调 callback
            return new NoopDisposable();
        };

        vm.NotifyMainWindowReady();

        Assert.False(vm.IsFading);  // timer 没触发 Tick → 不 fade
    }

    [Fact]
    public void NotifyMainWindowReady_AfterMinDisplayTime_TriggersFade()
    {
        // 通过直接调 StartFadeOut() 模拟 "timer 检测到 elapsed ≥ 3s"的内部路径
        var vm = new SplashViewModel("T", "sub", "v1.0");
        bool fadeCompletedFired = false;
        vm.FadeCompleted += () => fadeCompletedFired = true;

        vm.NotifyMainWindowReady();  // 启动 timer (默认无 TimerFactory → 不真启)
        vm.StartFadeOut();           // 直接模拟 timer 触发

        Assert.True(vm.IsFading);
        // StartFadeOut 本身不 raise FadeCompleted(那是 Storyboard.Completed 后调)
        Assert.False(fadeCompletedFired);
    }

    [Fact]
    public void RaiseFadeCompleted_FiresEventOnce()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        int fireCount = 0;
        vm.FadeCompleted += () => fireCount++;

        vm.RaiseFadeCompleted();
        vm.RaiseFadeCompleted();  // 二次触发(模拟 Storyboard→Close→Closed 双路径)

        Assert.Equal(1, fireCount);  // 幂等守卫生效
    }

    [Fact]
    public void NotifyMainWindowReady_AfterFade_NoOp()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        int timerCreated = 0;
        vm.TimerFactory = (_, _) =>
        {
            timerCreated++;
            return new NoopDisposable();
        };

        vm.StartFadeOut();  // 模拟 fade 已触发
        vm.RaiseFadeCompleted();
        vm.NotifyMainWindowReady();  // 已 disposed → TimerFactory 不调

        Assert.Equal(0, timerCreated);
    }
}