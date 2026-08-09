using System;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager;

public partial class MainWindow : Window
{
    private UiPreferences? _startupPrefs;

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
        }
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
}
