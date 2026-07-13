using System;
using System.Windows;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class LogViewerDialog : Window
{
    private readonly LogViewerViewModel? _vm;

    public LogViewerDialog(LogViewerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = $"日志 - {vm.EnvId}";
        Closed += (_, _) => _vm.Dispose();
    }

    /// <summary>
    /// 显示一个 env 的实时日志窗口。envId 只用于显示 / Dispose 时清理;
    /// tailer 由调用方决定 poll 间隔。
    /// </summary>
    public static void Show(string envId, string logFilePath,
        Window? owner = null, TimeSpan? pollInterval = null)
    {
        var tailer = new LogTailer(logFilePath, pollInterval);
        var vm = new LogViewerViewModel(envId, tailer);
        var dlg = new LogViewerDialog(vm)
        {
            Owner = owner ?? Application.Current.MainWindow,
        };
        dlg.Show();
    }
}
