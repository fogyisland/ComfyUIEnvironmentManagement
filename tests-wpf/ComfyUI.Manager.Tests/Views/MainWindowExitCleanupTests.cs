using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
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
                    new Settings(), null!, null!, null!, null!, null!, null!,
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

    // ===================================================================
    // v0.6.14 R2: OnClosing 早期返回回归测试
    // -------------------------------------------------------------------
    // R1 #3 把所有 hook 逻辑抽到 internal TryHandleExitCleanup,e.Cancel=true
    // (用户选 No) 时原 OnClosing 的 return 被吞,继续往下跑:
    //   1) ConfirmDiscardUnsavedSettings 弹第二个 modal
    //   2) UI prefs 持久化(写出 prefs 文件)
    // 测试只能通过 internal helper 验 cleanup,但验证 OnClosing 本体的 early-out
    // 必须直接调 OnClosing,所以这里用 reflection 调 private 方法。
    //
    // R2 fix:OnClosing 在 TryHandleExitCleanup 之后加 `if (e.Cancel) return;`。
    // ===================================================================

    /// <summary>
    /// STA helper,直接调 MainWindow.OnClosing (private) 验证 e.Cancel + 副作用。
    /// 比 event-raise 更稳:WPF headless Window 的 Closing event 不一定真 fire handler。
    /// </summary>
    private (MainWindow window, MainViewModel mvm, EnvExitCleanupService cleanup,
              EnvironmentRepository repo, UiPreferencesService prefs)
        RunOnClosingOnSTAWithSeededSettings(System.Action<(MainWindow w, MainViewModel mvm, EnvExitCleanupService c, EnvironmentRepository r)> setup)
    {
        MainWindow? window = null;
        MainViewModel? mvm = null;
        EnvExitCleanupService? cleanup = null;
        EnvironmentRepository? repo = null;
        UiPreferencesService? prefs = null;

        Exception? caught = null;
        StaFact.RunOnSTA(() =>
        {
            try
            {
                repo = new EnvironmentRepository(_db.Factory);
                var processStateRepo = new ProcessStateRepository(_db.Factory);
                var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
                cleanup = new EnvExitCleanupService(repo, launcher);
                prefs = new UiPreferencesService(_projectRoot);

                mvm = new MainViewModel(
                    _db.Factory,
                    launcher, null!, null!, null!, null!, null!, null!,
                    new Settings(), null!, null!, null!, null!, null!, null!,
                    null!, "", _projectRoot, null!, null!,
                    prefs,
                    envExitCleanup: cleanup,
                    envRepo: repo);

                // 强制缓存一份 SettingsViewModel,标 dirty 让 HasUnsavedChanges=true —
                // 如果 R2 fix 没生效,ConfirmDiscardUnsavedSettings 会进 UnsavedPromptOverride。
                var settingsField = typeof(MainViewModel).GetField(
                    "_settingsViewModel",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var svm = new SettingsViewModel(
                    new SettingsRepository(Path.Combine(_projectRoot, "settings-r2-test.json")),
                    new HttpProxyConfig(),
                    new PythonInterpreterValidator(),
                    new Settings());
                svm.Dirty.Mark("R2-test-dirty");
                settingsField.SetValue(mvm, svm);

                window = new MainWindow { DataContext = mvm };
                setup((window, mvm, cleanup, repo));

                // 通过 reflection 调 private OnClosing(object?, CancelEventArgs) — 模拟
                // WPF 真的 raise Closing event。headless Window 的 event raise 不可靠
                // (无 HwndSource),直接调 handler 是 R1 之前测试的同款写法。
                // DeclaredOnly 排除 Window 基类的同名 protected override,避免
                // AmbiguousMatchException。
                var mi = typeof(MainWindow).GetMethod(
                    "OnClosing",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Assert.NotNull(mi);
                var e = new CancelEventArgs();
                mi!.Invoke(window, new object?[] { window, e });
                FlushDispatcher();

                Assert.True(e.Cancel, "R2 fix 验证点:OnClosing 跑完后 e.Cancel 必须 true");
                Assert.True(svm.HasUnsavedChanges, "fixture 验证:Dirty 必须留着(否则 ConfirmDiscardUnsavedSettings 短路)");
            }
            catch (Exception ex) { caught = ex; }
        });

        if (caught is not null) throw caught;
        return (window!, mvm!, cleanup!, repo!, prefs!);
    }

    [Fact]
    public void OnClosing_R2_UserCancelsShutdownConfirm_DoesNotInvokeUnsavedPrompt()
    {
        // 关键回归:用户在 exit cleanup confirm 选 No 后,ConfirmDiscardUnsavedSettings
        // 不应该被调(否则弹第二个 modal)。UnsavedPromptOverride 被设了 counter spy,
        // 调用次数 = 0 证明整个 settings 拦截路径被早返短路。
        var unsavedPromptCalls = 0;
        var (_, mvm, cleanup, repo, _) = RunOnClosingOnSTAWithSeededSettings(t =>
        {
            t.r.Upsert(MakeRunningEnv("env-r2-cancel"));
            t.c.ConfirmShutdown = _ => false;   // user: No
            t.mvm.UnsavedPromptOverride = _ =>
            {
                unsavedPromptCalls++;
                return MainViewModel.UnsavedChoice.Cancel;
            };
        });

        Assert.Equal(0, unsavedPromptCalls);  // R2 回归:UnsavedPromptOverride 不该被调(early-out 后 ConfirmDiscardUnsavedSettings 不进)
        // env 没动(stop 没跑)
        Assert.Equal("running", repo.ListAll().Single().Status);
    }

    [Fact]
    public void OnClosing_R2_UserCancelsShutdownConfirm_DoesNotWriteUiPrefs()
    {
        // 关键回归:用户在 exit cleanup confirm 选 No 后,UI prefs 不应该被持久化。
        // 同时 wire App.UiPreferencesService + ApplyStartupPreferences 让 prefs 路径
        // 真有东西可写(R2 没生效时 SaveToFile 会跑)。SaveToFile 非 virtual,所以用
        // 文件是否存在作为 spy:R2 没生效时 prefs 文件会被写出,R2 fix 后保持不存在。
        Exception? caught = null;

        StaFact.RunOnSTA(() =>
        {
            try
            {
                var repo = new EnvironmentRepository(_db.Factory);
                var processStateRepo = new ProcessStateRepository(_db.Factory);
                var launcher = new ProcessLauncher(_projectRoot, _db.Factory, repo, processStateRepo);
                var cleanup = new EnvExitCleanupService(repo, launcher);

                var prefsService = new UiPreferencesService(_projectRoot);
                // DefaultPath = <projectRoot>/config/ui-preferences.json — 用它作 spy:
                // R2 fix 没生效时 SaveToFile 会创建该文件,有 fix 时不创建。
                var writeTargetPath = prefsService.DefaultPath;
                if (File.Exists(writeTargetPath)) File.Delete(writeTargetPath);

                // App.UiPreferencesService 是 static private set,reflection 注入。
                var svcProp = typeof(App).GetProperty(
                    "UiPreferencesService",
                    BindingFlags.Public | BindingFlags.Static)!;
                var originalSvc = svcProp.GetValue(null);
                svcProp.SetValue(null, prefsService);

                try
                {
                    var mvm = new MainViewModel(
                        _db.Factory,
                        launcher, null!, null!, null!, null!, null!, null!,
                        new Settings(), null!, null!, null!, null!, null!, null!,
                        null!, "", _projectRoot, null!, null!,
                        prefsService,
                        envExitCleanup: cleanup,
                        envRepo: repo);

                    repo.Upsert(MakeRunningEnv("env-r2-prefs-no-write"));
                    cleanup.ConfirmShutdown = _ => false;

                    var window = new MainWindow { DataContext = mvm };
                    // 给 Window 设具体尺寸 — headless Window.Width 默认 NaN,
                    // SaveToFile JSON 序列化会抛 ArgumentException(infinity 不能写),
                    // prefs 路径永远到不了。设具体值让 prefs 路径真能跑。
                    window.Width = 800;
                    window.Height = 600;
                    window.Left = 100;
                    window.Top = 100;
                    // _startupPrefs 必须非 null(否则 prefs 路径立即 return,无法测)
                    window.ApplyStartupPreferences(new UiPreferences
                    {
                        WindowWidth = 800,
                        WindowHeight = 600,
                        WindowLeft = 100,
                        WindowTop = 100,
                        WindowMaximized = false,
                    });

                    var mi = typeof(MainWindow).GetMethod(
                        "OnClosing",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    var e = new CancelEventArgs();

                    try
                    {
                        mi!.Invoke(window, new object?[] { window, e });
                    }
                    catch (System.Reflection.TargetInvocationException tie)
                    {
                        throw tie.InnerException ?? tie;
                    }
                    FlushDispatcher();

                    Assert.True(e.Cancel, "OnClosing 必须 e.Cancel=true");

                    // Spy:文件不存在 = R2 fix 让 prefs 路径没跑到
                    Assert.False(File.Exists(writeTargetPath),
                        $"R2 回归:OnClosing 在 e.Cancel=true 后应 early-out,但 prefs 文件 {writeTargetPath} 被写出");
                }
                finally
                {
                    svcProp.SetValue(null, originalSvc);
                }
            }
            catch (Exception ex) { caught = ex; }
        });

        if (caught is not null) throw caught;
    }
}