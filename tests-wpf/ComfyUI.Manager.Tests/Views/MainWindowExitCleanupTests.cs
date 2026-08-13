using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.14 T6: MainWindow.OnClosing 退出清理 hook 集成测试。
///
/// 验证 4 个不变量:
/// 1. 没 running env → 不弹 confirm,e.Cancel=false
/// 2. running env + ConfirmShutdown=false → e.Cancel=true,env 没动
/// 3. running env + ConfirmShutdown=true → 异步 cleanup 触发(env 翻 stopped)
/// 4. ConfirmShutdownOverride 直接控制 confirm,不依赖 DefaultConfirm MessageBox
///
/// 走 StaFact.RunOnSTA — MainWindow 构造需要 STA 线程 + WPF Application 单例。
/// </summary>
public class MainWindowExitCleanupTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainWindowExitCleanupTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "mainwindow-exit-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private static Environment MakeRunningEnv(string id) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = "running",
        };

    /// <summary>
    /// 在 STA thread 上构造 MainWindow(需要 WPF Dispatcher)+ seeded env。
    /// 返回(window, cleanup, repo, e.Cancel)——OnClosing 跑完后回填 e.Cancel。
    /// </summary>
    private (MainWindow window, EnvExitCleanupService cleanup, EnvironmentRepository repo, bool cancelObserved)
        RunOnClosingOnSTA(System.Action<(MainWindow w, EnvExitCleanupService c, EnvironmentRepository r)> setup)
    {
        MainWindow? window = null;
        EnvExitCleanupService? cleanup = null;
        EnvironmentRepository? repo = null;
        bool cancelObserved = false;

        Exception? caught = null;
        StaFact.RunOnSTA(() =>
        {
            try
            {
                // 在 STA thread 上构造 — MainWindow ctor 调 FrameworkElement..ctor
                // 拿 InputManager,需要 STA。
                repo = new EnvironmentRepository(_db.Factory);
                var processStateRepo = new ProcessStateRepository(_db.Factory);
                var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
                cleanup = new EnvExitCleanupService(repo, launcher);

                var mvm = new MainViewModel(
                    _db.Factory,
                    launcher, null!, null!, null!, null!, null!, null!,
                    new Settings(), null!, null!, null!, null!, null!,
                    null!, "", _projectRoot, null!, null!,
                    new UiPreferencesService(_projectRoot),
                    envExitCleanup: cleanup,
                    // v0.6.14 R1: GetRunningEnvCount 走 _envRepo.CountByStatus,
                    // 测试必须 wire repo,否则 count 返 0 跳过 cleanup 路径。
                    envRepo: repo);

                window = new MainWindow { DataContext = mvm };
                setup((window, cleanup, repo));

                var e = new CancelEventArgs();
                // v0.6.14 R1: 直接调 internal TryHandleExitCleanup —— 不再用
                // BindingFlags.DeclaredOnly 反射 private OnClosing(避开 WPF
                // Window 基类同名 protected virtual 的 brittle 假设)。
                // OnClosing event handler 在 ctor 里 wired,它内部委托给本方法。
                window.TryHandleExitCleanup(e);

                // Flush Dispatcher — 异步 cleanup 在 InvokeAsync 队列上跑
                FlushDispatcher();
                cancelObserved = e.Cancel;
            }
            catch (Exception ex) { caught = ex; }
        });

        if (caught is not null) throw caught;
        return (window!, cleanup!, repo!, cancelObserved);
    }

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void OnClosing_NoRunningEnvs_NoConfirmDialog_ClosesWindow()
    {
        var confirmCalled = false;
        var (_, cleanup, _, cancelObserved) = RunOnClosingOnSTA(t =>
        {
            t.c.ConfirmShutdown = _ => { confirmCalled = true; return true; };
        });

        Assert.False(cancelObserved, "没 running env 时,e.Cancel 应该保持 false");
        Assert.False(confirmCalled, "没 running env 时,ConfirmShutdown 不该被调");
    }

    [Fact]
    public void OnClosing_RunningEnvs_UserCancelsConfirm_eCancelTrue()
    {
        var confirmCalled = false;
        var receivedCount = -1;
        var (_, cleanup, repo, cancelObserved) = RunOnClosingOnSTA(t =>
        {
            t.r.Upsert(MakeRunningEnv("env-running-cancel"));
            t.c.ConfirmShutdown = count =>
            {
                confirmCalled = true;
                receivedCount = count;
                return false;  // user: No — 不要退
            };
        });

        Assert.True(confirmCalled, "ConfirmShutdown 必须被调");
        Assert.Equal(1, receivedCount);
        Assert.True(cancelObserved, "用户选 No 时,e.Cancel 必须 true");
        // env 状态不变(stop 没跑)
        Assert.Equal("running", repo.ListAll().Single().Status);
    }

    [Fact]
    public void OnClosing_RunningEnvs_UserProceedsConfirm_TriggersAsyncCleanup()
    {
        var confirmCalled = false;
        var (_, cleanup, repo, cancelObserved) = RunOnClosingOnSTA(t =>
        {
            t.r.Upsert(MakeRunningEnv("env-running-proceed-1"));
            t.r.Upsert(MakeRunningEnv("env-running-proceed-2"));
            t.c.ConfirmShutdown = count => { confirmCalled = true; return true; };
        });

        Assert.True(confirmCalled, "ConfirmShutdown 必须被调");
        Assert.False(cancelObserved, "用户选 Yes 时,e.Cancel 保持 false");
        // 异步 cleanup 跑完后,env 应翻 stopped
        Assert.All(repo.ListAll(), e => Assert.Equal("stopped", e.Status));
    }

    [Fact]
    public void OnClosing_ConfirmShutdownOverride_ReturnsFalse_AbortsExit()
    {
        // 同 test #2,但显式声明"override 模式 + false → 中止"
        var overrideCallCount = 0;
        var (_, cleanup, repo, cancelObserved) = RunOnClosingOnSTA(t =>
        {
            t.r.Upsert(MakeRunningEnv("env-override-false"));
            t.c.ConfirmShutdown = count =>
            {
                overrideCallCount++;
                return false;
            };
        });

        Assert.Equal(1, overrideCallCount);
        Assert.True(cancelObserved);
        // DefaultConfirm 没被调 — override 模式下走 ConfirmShutdown 直接 false。
        Assert.Equal("running", repo.ListAll().Single().Status);
    }
}