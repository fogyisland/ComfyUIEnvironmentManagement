using System;
using System.ComponentModel;
using System.Threading.Tasks;
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
    /// <summary>
    /// 测试 seam:生产代码 Show 弹 WPF Window 阻塞 UI 线程;
    /// 单测可赋值 ShowOverride 模拟 picker 行为(返 stub entry + 捕获回调)。
    /// v0.6.14 T5:扩展签名,接收 onInstallSuccess + onClosed 让测试验 wiring。
    /// </summary>
    public static Func<
        EnvironmentRepository,
        NodeOperations,
        CatalogRepository,
        NodeRepository,
        NodeVersionRepository,
        AppLogger?,
        string,
        Func<string, Task>?,
        Action?,
        CatalogEntry?>? ShowOverride { get; set; }

    private bool _isClosingFromUser;

    protected override void OnClosing(CancelEventArgs e)
    {
        // v0.6.15 hotfix:WM_CLOSE 路径中(用户按 X / Alt+F4),Closing 事件 handler
        // 会调 vm.RaiseClosed() → vm.Closed → ctor 的 Close() handler。第二次
        // Close() 在 WM_CLOSE 中抛 InvalidOperationException。flag 让 ctor handler
        // 在 closing 路径上短路,只让 CancelCommand 路径正常关 dialog。
        _isClosingFromUser = true;
        base.OnClosing(e);
    }

    public CatalogEntryPickerDialog(CatalogEntryPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // v0.6.14 T5:不再有 OkCommand / Cancelled — picker 自身管 install,关 dialog
        // 只通过 CancelCommand / X / Alt+F4 → RaiseClosed() 触发 Closed。
        // v0.6.15 hotfix:X / Alt+F4 路径 OnClosing 已置 _isClosingFromUser=true →
        // 本 handler 短路,避免重复 Close()。CancelCommand 路径正常设值 + Close()。
        vm.Closed += () =>
        {
            if (_isClosingFromUser) return;
            DialogResult = true;
            Close();
        };
    }

    /// <summary>
    /// Show(envRepo, nodeOps, catalogRepo, nodeRepo, versionRepo, logger, envId,
    /// onInstallSuccess, onClosed):打开 picker,绑定到指定 env 的安装状态。
    ///
    /// v0.6.14 T5:不再返回有效 CatalogEntry 给 caller 弹 InstallDialog — 安装直接在
    /// picker 行内完成(InstallCommand)。返回值保留 CatalogEntry? 类型是为向后兼容
    /// (旧的 ShowOverride 测试 seam 仍能返 entry),生产路径忽略返回值。
    ///
    /// onInstallSuccess:行内安装成功后 fire-and-forget 触发(等同 v0.6.11 InstallDialog
    /// 的同名参数 — caller 典型传 MainViewModel.RestartEnvAsync)。
    /// onClosed:picker 关后(任意路径)fire 一次,caller 用来刷新 env-list。
    /// </summary>
    public static CatalogEntry? Show(
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRepository catalogRepo,
        NodeRepository nodeRepo,
        NodeVersionRepository versionRepo,
        AppLogger? logger,
        string envId,
        Func<string, Task>? onInstallSuccess = null,
        Action? onClosed = null)
    {
        if (ShowOverride is not null)
            return ShowOverride(envRepo, nodeOps, catalogRepo, nodeRepo, versionRepo, logger, envId, onInstallSuccess, onClosed);

        var vm = new CatalogEntryPickerViewModel(
            catalogRepo, nodeRepo, nodeOps, versionRepo, envId, logger, onInstallSuccess);
        // onClosed 在 dialog 实际关闭时 fire 一次(任意路径)。
        // VM 的 CancelCommand 已经 fire Closed;这里再 hook dialog 的 Closing 覆盖
        // X 按钮 / Alt+F4 路径。
        if (onClosed is not null)
        {
            vm.Closed += onClosed;
            var dlg = new CatalogEntryPickerDialog(vm)
            {
                Owner = Application.Current.MainWindow,
            };
            dlg.Closing += (_, _) => vm.RaiseClosed();
            dlg.ShowDialog();
            return null;  // v0.6.14 T5:不再返 entry 给 caller 弹 InstallDialog
        }

        var dlg2 = new CatalogEntryPickerDialog(vm)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg2.ShowDialog();
        return null;
    }

    /// <summary>
    /// v0.6.14 R1 fix:filter chip 点击处理。
    ///
    /// RadioButton.IsChecked 走 OneWay binding,用户点击不会自动 set VM.ActiveFilter
    /// (Critical 1 — WPF 不支持 enum � RadioButton.IsChecked 的双向 binding)。
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
    /// v0.6.14 R1 fix + T5:ListBox 行双击触发安装(只对未装条目生效,
    /// InstallCommand.CanExecute 已 gate IsInstalled==false && !Busy && !IsInstalling)。
    /// </summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CatalogEntryPickerViewModel vm) return;
        // 选中行不是 ListBoxItem 时(sender 可能是 header 等)e.OriginalSource 兜底
        var item = vm.Selected;
        if (item is null) return;
        if (vm.InstallCommand.CanExecute(item))
        {
            vm.InstallCommand.Execute(item);
        }
    }
}
