using System.ComponentModel;
using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BulkUpdateDialog : Window
{
    public BulkUpdateDialog(BulkUpdateDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static void Show(BulkUpdateDialogViewModel vm, Window? owner = null)
    {
        var dlg = new BulkUpdateDialog(vm)
        {
            Owner = owner ?? Application.Current.MainWindow,
        };
        // 用户关窗时若 run 还在跑,通知 orchestrator 取消 —— 否则
        // Task.Run 跑到底才结束,BackgroundQueue 上持续 emit 事件。
        dlg.Closing += (_, e) =>
        {
            if (vm.IsBusy)
            {
                vm.CancelRun();
            }
        };
        dlg.ShowDialog();
    }
}
