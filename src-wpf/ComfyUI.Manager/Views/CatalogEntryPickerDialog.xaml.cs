using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class CatalogEntryPickerDialog : Window
{
    public CatalogEntry? Result { get; private set; }

    /// <summary>
    /// 测试 seam:生产代码 ShowDialog 弹 WPF Window 阻塞 UI 线程;
    /// 单测可赋值 ShowOverride 模拟用户选择或取消。
    /// </summary>
    public static Func<
        EnvironmentRepository,
        NodeOperations,
        CatalogRepository,
        NodeRepository,
        AppLogger?,
        string,
        CatalogEntry?>? ShowOverride { get; set; }

    public CatalogEntryPickerDialog(CatalogEntryPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseWithEntry += entry =>
        {
            Result = entry;
            DialogResult = true;
            Close();
        };
        vm.Cancelled += () =>
        {
            Result = null;
            DialogResult = false;
            Close();
        };
    }

    /// <summary>
    /// Show(envRepo, nodeOps, catalogRepo, nodeRepo, logger, envId):打开 picker,
    /// 绑定到指定 env 的安装状态。envId 非空时 picker 知道哪些 catalog 条目已
    /// 装入此 env,显示"已装"/"已过时" 徽标 + 行内卸载按钮。
    ///
    /// 取消返回 null;选中未装条目(Ok / 双击未装条目 / 点行内"安装"按钮)返回 CatalogEntry,
    /// 由 caller 接着弹 InstallDialog。repos 全部由 caller 注入,保证 picker 跟
    /// 其他 view 共享同一份 db 连接 / service 实例。
    ///
    /// onClosed: picker 关后(任意路径)fire 一次,caller 用来刷新 env-list。
    /// </summary>
    public static CatalogEntry? Show(
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRepository catalogRepo,
        NodeRepository nodeRepo,
        AppLogger? logger,
        string envId,
        Action? onClosed = null)
    {
        if (ShowOverride is not null)
            return ShowOverride(envRepo, nodeOps, catalogRepo, nodeRepo, logger, envId);

        var vm = new CatalogEntryPickerViewModel(
            catalogRepo, nodeRepo, nodeOps, envId, logger);
        // v0.6.14 T3:onClosed 在 dialog 实际关闭时 fire 一次(任意路径)。
        // VM 的 OkCommand / CancelCommand 已经 fire Closed;
        // 这里再 hook dialog 的 Closing 覆盖 X 按钮 / Alt+F4 路径。
        if (onClosed is not null)
        {
            vm.Closed += onClosed;
            var dlg = new CatalogEntryPickerDialog(vm)
            {
                Owner = Application.Current.MainWindow,
            };
            dlg.Closing += (_, _) => vm.RaiseClosed();
            dlg.ShowDialog();
            return dlg.Result;
        }

        var dlg2 = new CatalogEntryPickerDialog(vm)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg2.ShowDialog();
        return dlg2.Result;
    }

    /// <summary>
    /// v0.6.14 R1 fix:filter chip 点击处理。
    ///
    /// RadioButton.IsChecked 走 OneWay binding,用户点击不会自动 set VM.ActiveFilter
    /// (Critical 1 — WPF 不支持 enum ↔ RadioButton.IsChecked 的双向 binding)。
    /// 这里在 Click 事件里把 Tag(PickerFilterOption wrapper)上的 Filter enum
    /// 写回 VM.ActiveFilter,触发 ApplyFilter() rebuild Items。
    /// </summary>
    private void OnFilterChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is PickerFilterOption opt
            && DataContext is CatalogEntryPickerViewModel vm)
        {
            vm.ActiveFilter = opt.Filter;
        }
    }

    /// <summary>
    /// v0.6.14 R1 fix:ListBox 行双击触发 OkCommand(只对未装条目生效,
    /// OkCommand.CanExecute 已 gate IsInstalled==false && !Busy)。
    /// </summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CatalogEntryPickerViewModel vm) return;
        // 选中行不是 ListBoxItem 时(sender 可能是 header 等)e.OriginalSource 兜底
        var item = vm.Selected;
        if (item is null) return;
        if (vm.OkCommand.CanExecute(null))
        {
            vm.OkCommand.Execute(null);
        }
    }
}
