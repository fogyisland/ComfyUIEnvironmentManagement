using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProgressDialog : Window
{
    private readonly BaseEnvProgressViewModel _vm;

    public BaseEnvProgressDialog(
        IReadOnlyList<string> envIds,
        BaseEnvProfile profile,
        BaseEnvInstaller installer)
    {
        InitializeComponent();
        _vm = new BaseEnvProgressViewModel(envIds, profile, installer);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunAsync();
            }
            catch { /* errors are surfaced via OverallStatus */ }
        };
        Closing += OnClosing;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// v0.6.6.2 hotfix:install 还在跑时拦截窗口关闭(✕ 按钮 / Alt+F4 等)— Close 按钮
    /// 已被 IsEnabled 禁用,但 ✕ 走 Window.Closing 事件,这里也拒绝。同时弹 MessageBox
    /// 告知用户在跑 — 否则用户看到 dialog 不动也没反馈,会以为 install 已挂。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.IsInstallRunning) return;
        e.Cancel = true;
        MessageBox.Show(
            "基础环境正在安装,请等待完成(成功 / 失败 / 取消)后再关闭窗口。",
            "正在安装",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 静态入口:弹 progress dialog,fire-and-forget,完成后用户点"关闭"。
    /// 内部 fire-and-forget _vm.RunAsync(),Close 按钮和 Cancel 走 vm 命令。
    /// </summary>
    public static void Show(
        IReadOnlyList<string> envIds,
        BaseEnvProfile profile,
        BaseEnvInstaller installer)
    {
        var dlg = new BaseEnvProgressDialog(envIds, profile, installer)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }
}
