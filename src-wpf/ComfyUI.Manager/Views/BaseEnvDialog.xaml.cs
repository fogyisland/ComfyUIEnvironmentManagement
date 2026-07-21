using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvDialog : Window
{
    public BaseEnvDialogResult? Result { get; private set; }

    public BaseEnvDialog(BaseEnvDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += result =>
        {
            Result = result;
            DialogResult = result is not null;
            Close();
        };
    }

    /// <summary>
    /// 静态入口:用 env 列表 + 当前 Settings.BaseEnv 副本打开 dialog。
    /// 返回 null = 用户取消 / 关闭,否则返回 result(payload:envIds + config)。
    /// </summary>
    public static BaseEnvDialogResult? Show(IList<Environment> envs, Settings settings)
    {
        var vm = new BaseEnvDialogViewModel(envs, settings.BaseEnv);
        var dlg = new BaseEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
}