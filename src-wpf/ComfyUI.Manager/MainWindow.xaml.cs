using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ComfyUI.Manager.Animations;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class MainWindow : Window
{
    private UiPreferences? _startupPrefs;

    // v0.6.9 T9:主题切换 cross-fade 互斥锁 — fade 中再点主题切换会闪烁,
    // 用 flag 串行化:收到 ThemeChanging 时若已有正在跑的 fade,先停旧的再开新的。
    private Storyboard? _activeThemeFade;
    private bool _themeFading;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        // v0.6.9 T5:Dashboard 设为启动默认页。Loaded 在 DataContext 已建立
        // (MainViewModel via App.OnStartup) 之后 fire,ShowDashboardCommand.Execute
        // 同步切 CurrentSection=MainSection.Dashboard + CurrentView=DashboardView。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // RelayCommand.Execute 是 sync Action — ShowDashboard 内部 fire-and-forget
            // _ = _dashboardViewModel.RefreshAsync(); 走后台,UI 立即可用。
            vm.ShowDashboardCommand.Execute(null);

            // v0.6.9 T7:Ctrl+K 绑 OpenSpotlightCommand — 等 DataContext 就绪再注入,
            // XAML KeyBinding 引用不到 ctor 时尚未存在的 MainViewModel。
            // Esc 绑 CloseSpotlightCommand 关 popup。
            InputBindings.Add(new KeyBinding
            {
                Key = Key.K,
                Modifiers = ModifierKeys.Control,
                Command = vm.OpenSpotlightCommand,
            });
            InputBindings.Add(new KeyBinding
            {
                Key = Key.Escape,
                Command = vm.CloseSpotlightCommand,
            });
        }

        // v0.6.9 T9:订阅 ThemeService.ThemeChanging 触 cross-fade。G9 short-circuit
        // 在 OnThemeChanging 顶部 IsAnimationEnabled 检查 — 系统关动效直接 return。
        var themeService = ((App)Application.Current).ThemeService;
        if (themeService is not null) themeService.ThemeChanging += OnThemeChanging;
    }

    /// <summary>
    /// v0.6.9 T9:主题切换 cross-fade handler。ThemeService.Apply 在 swap palette 前
    /// broadcast ThemeChanging,所以这里跑 fade-out → ThemeService 内部 swap palette
    // → fade-in。G9:IsAnimationEnabled=false 直接 return,palette 仍会 swap(ThemeService
    // 自己的逻辑),只是没视觉过渡。
    /// </summary>
    private void OnThemeChanging(object? sender, ThemeMode e)
    {
        if (!MotionSettings.IsAnimationEnabled) return;
        if (_themeFading) return;  // 互斥锁:fade 中再点主题切换被忽略(ThemeService 仍会 swap)

        _themeFading = true;

        // 停掉旧 sb(避免重叠);开始 fade-out → swap 已发生 → fade-in
        _activeThemeFade?.Stop(ThemeCrossfadeOverlay);

        var fadeOut = new DoubleAnimation(0, 1, MotionSettings.DurationThemeCrossfade);
        Storyboard.SetTarget(fadeOut, ThemeCrossfadeOverlay);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));
        var sbOut = new Storyboard { Duration = MotionSettings.DurationThemeCrossfade };
        sbOut.Children.Add(fadeOut);
        sbOut.Completed += (_, _) =>
        {
            // fade-out 完成 → ThemeService 已经 swap palette(因为它先 broadcast 然后 Apply)。
            // 现在跑 fade-in 把 overlay 透明回去。
            var fadeIn = new DoubleAnimation(1, 0, MotionSettings.DurationThemeCrossfade);
            Storyboard.SetTarget(fadeIn, ThemeCrossfadeOverlay);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));
            var sbIn = new Storyboard { Duration = MotionSettings.DurationThemeCrossfade };
            sbIn.Children.Add(fadeIn);
            sbIn.Completed += (_, _) =>
            {
                _themeFading = false;
                _activeThemeFade = null;
            };
            _activeThemeFade = sbIn;
            sbIn.Begin(ThemeCrossfadeOverlay);
        };
        _activeThemeFade = sbOut;
        sbOut.Begin(ThemeCrossfadeOverlay);
    }

    /// <summary>
    /// App.OnStartup 在 Show() 之前调一次,把启动 prefs 存进 Window 实例,
    /// SourceInitialized 时把 Width/Height/Left/Top/Maximized 应用上(G8)。
    /// </summary>
    public void ApplyStartupPreferences(UiPreferences prefs)
    {
        _startupPrefs = prefs;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var p = _startupPrefs;
        if (p is null) return;

        // 位置越界(多显示器移除场景)→ 退到 (100,100);尺寸合法性同理
        var left = p.WindowLeft ?? 100;
        var top = p.WindowTop ?? 100;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        if (left < 0 || left > vw - 100) left = 100;
        if (top < 0 || top > vh - 50) top = 100;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;

        if (p.WindowWidth is double w && w >= 200) Width = w;
        if (p.WindowHeight is double h && h >= 150) Height = h;
        if (p.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // v0.6.14 R1: 把所有 hook 逻辑拆到 TryHandleExitCleanup,测试可直接调
        // internal 方法,不再用 BindingFlags.DeclaredOnly 反射 private OnClosing。
        TryHandleExitCleanup(e);

        // v0.6.14 R2: 早期返回 —— 用户在 exit cleanup confirm 选 No 时,
        // TryHandleExitCleanup 已经 e.Cancel=true;后面的 settings 未保存
        // 检查 + UI prefs 持久化不能跑(否则用户看到第二次 modal 且 prefs 被写)。
        if (e.Cancel) return;

        // v0.6.11+ SDD B T3:settings 未保存改动拦截。
        // 切侧栏离开 settings tab 不拦(VM 缓存 + dirty 状态活着),
        // 只在主窗口关闭时一次性拦:Yes=Save&Exit, No=Discard&Exit, Cancel=留下。
        if (DataContext is MainViewModel mvm2 && !mvm2.ConfirmDiscardUnsavedSettings())
        {
            e.Cancel = true;
            return;
        }

        // 写回 prefs(G8)— 只读当前 Window 状态(简化版,完整版本由
        // LastSelectedEnvId 在 MainViewModel 维护)
        var svc = App.UiPreferencesService;
        if (svc is null || _startupPrefs is null) return;
        var write = new UiPreferences
        {
            // WindowState 还原(避免记 Normal 时存 Maximized 还原时又把窗口当 Normal)
            WindowWidth = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowWidth : Width,
            WindowHeight = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowHeight : Height,
            WindowLeft = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowLeft : Left,
            WindowTop = WindowState == WindowState.Maximized
                ? _startupPrefs.WindowTop : Top,
            WindowMaximized = WindowState == WindowState.Maximized,
            SidebarVisible = _startupPrefs.SidebarVisible,
            LastSelectedEnvId = _startupPrefs.LastSelectedEnvId,
            LastViewName = _startupPrefs.LastViewName,
        };
        svc.SaveToFile(svc.DefaultPath, write);
    }

    /// <summary>
    /// v0.6.14 R1: 退出清理 hook(从 OnClosing 抽出,internal 便于测试直接调用)。
    /// 处理流程:
    /// - 没 cleanup service(测试 ctor 没传)→ 走老路径(空操作,return)
    /// - running count == 0 → 跳过 confirm,直接清理(空循环,啥也不做)
    /// - 用户 No → e.Cancel=true,留下
    /// - 用户 Yes → 异步 cleanup(Dispatcher.InvokeAsync,window close 不阻塞),
    ///   App.OnExit 的 _launcher.Dispose 兜底任何没跑完的进程。
    ///
    /// v0.6.14 R1: 异步 cleanup 异常不再静默吞掉 — 用 EnvExitCleanupService.Logger
    /// 写 <c>[env-exit-failed]</c> 标签。OCE 也不 rethrow(这是 exit path,无 caller
    /// 监听 rethrow),所有异常走 logger 兜底。
    /// </summary>
    internal void TryHandleExitCleanup(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not MainViewModel mvm) return;
        var cleanup = mvm.GetExitCleanupService();
        if (cleanup is null) return;

        var runningCount = mvm.GetRunningEnvCount();
        if (runningCount > 0)
        {
            var proceed = cleanup.ConfirmShutdown?.Invoke(runningCount) ?? cleanup.DefaultConfirm(runningCount);
            if (!proceed)
            {
                e.Cancel = true;
                return;
            }
        }
        // 异步跑 cleanup — window 立即关闭,后台 stop + 翻 status。
        // Dispatcher 关闭后 InvokeAsync 不一定 fire,App.OnExit 兜底。
        var logger = cleanup.Logger;
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await cleanup.ShutdownRunningEnvsAsync();
            }
            catch (Exception ex)
            {
                // R1: 退出期 stop 失败 — 写日志(用 cleanup 自己的 logger,跟
                // 主流程同一份),不再静默吞。App.OnExit → _launcher.Dispose
                // force-kill 仍然兜底,这里只负责记录。
                logger?.Error("env-exit-failed",
                    $"exit cleanup threw {ex.GetType().Name}: {ex.Message}", ex);
            }
        });
    }

    // v1.0.0 T11: removed OnTemplateUpdateStatusCloseClicked — 工具菜单 → 模板更新
    // 全局状态面板删除, 模板更新改到 TemplateManagementView 每张卡 "更新源码" 按钮。
}
