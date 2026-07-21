using System.Collections.Generic;
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
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        InitializeComponent();
        _vm = new BaseEnvProgressViewModel(envIds, config, installer);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunAsync();
            }
            catch { /* errors are surfaced via OverallStatus */ }
        };
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 静态入口:弹 progress dialog,fire-and-forget,完成后用户点"关闭"。
    /// 内部 fire-and-forget _vm.RunAsync(),Close 按钮和 Cancel 走 vm 命令。
    /// </summary>
    public static void Show(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        var dlg = new BaseEnvProgressDialog(envIds, config, installer)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }
}
