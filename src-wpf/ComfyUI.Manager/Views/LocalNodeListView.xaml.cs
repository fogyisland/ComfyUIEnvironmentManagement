using System;
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class LocalNodeListView : UserControl
{
    public LocalNodeListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.LocalNodeListViewModel vm)
            {
                // 首次进入自动 refresh(RelayCommand.Execute 返回 void,fire-and-forget)
                vm.RefreshCommand.Execute(null);
                // v0.6.15.7:T2 — LogLines 新行追加时 ScrollViewer 自动滚到底。
                // NodeRequirementsStatus 可能 VM 初始化时已经 set,也可能在后续赋值,所以
                // 既 hook 当前实例,又订阅 PropertyChanged 监听后续赋值。
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ViewModels.LocalNodeListViewModel.NodeRequirementsStatus))
                    {
                        HookLogScroll(vm.NodeRequirementsStatus);
                    }
                };
                HookLogScroll(vm.NodeRequirementsStatus);
            }
        };
    }

    /// <summary>
    /// v0.6.15.6:关闭 inline 装依赖面板。Border 的 DataContext 是
    /// NodeRequirementsStatusViewModel 实例,沿视觉树往上找 ListBox 的 DataContext
    /// (即 LocalNodeListViewModel) 来清 NodeRequirementsStatus。
    /// </summary>
    private void OnCloseNodeRequirementsClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.LocalNodeListViewModel vm && vm.NodeRequirementsStatus is not null)
        {
            vm.NodeRequirementsStatus.Hide();
        }
    }

    /// <summary>
    /// v0.6.15.7:T2 — NodeRequirementsStatus.LogLines 新行追加时 ScrollViewer 自动滚到底。
    /// LocalNodeListViewModel 设 LogLines 时不直接调到这里 — 走 ItemsSource binding +
    /// CollectionChanged 订阅。
    /// </summary>
    private void ScrollLogToEnd() => LogScrollViewer.ScrollToEnd();

    private void HookLogScroll(ViewModels.NodeRequirementsStatusViewModel? status)
    {
        if (status is null) return;
        status.LogLines.CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(ScrollLogToEnd));
        };
    }
}