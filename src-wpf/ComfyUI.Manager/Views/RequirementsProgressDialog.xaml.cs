using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class RequirementsProgressDialog : Window
{
    private readonly RequirementsProgressViewModel _vm;

    public RequirementsProgressDialog(Environment env, RequirementsInstaller installer)
    {
        InitializeComponent();
        _vm = new RequirementsProgressViewModel(env, installer);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            try
            {
                var result = await _vm.RunAsync();
                _vm.OnCompleted(result);
            }
            catch
            {
                // installer 内部 try/catch,异常极少;真出异常就静默(状态显示 Failed)
            }
        };
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 静态入口:弹 RequirementsProgressDialog,fire-and-forget,完成后用户点"关闭"。
    /// </summary>
    public static void Show(Environment env, RequirementsInstaller installer)
    {
        var dlg = new RequirementsProgressDialog(env, installer)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }
}
