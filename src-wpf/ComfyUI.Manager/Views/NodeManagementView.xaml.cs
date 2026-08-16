using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Views;

public partial class NodeManagementView : UserControl
{
    public NodeManagementView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// v0.6.15.10:每行加载时订阅 DataContext (ScannedNode) 的 PropertyChanged。
    /// RowDetailsVisible 翻了 → 手动改 DataGridRow.RowDetailsVisibility enum。
    /// <para>
    /// 原因:<c>RowDetailsVisibility</c> 不是 DependencyProperty,XAML <c>Style.Setter</c>
    /// 绑不上(.NET 8 XAML parser 报 MC4005);<c>RowDetailsVisibilityBinding</c> 也是
    /// .NET 8 WPF DataGrid 不认的属性(MC3072)。只能 code-behind 监听。
    /// </para>
    /// </summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is ScannedNode node)
        {
            // 初次加载立刻同步一次(后面 collection rebuild 后再 load,值已经是新的)
            // 注:WPF DataGridRow 上是 DetailsVisibility(CLR property),DataGrid 上才是 RowDetailsVisibility。
            e.Row.DetailsVisibility = node.RowDetailsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            node.PropertyChanged += OnNodePropertyChanged;
        }
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is ScannedNode node)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
        }
    }

    /// <summary>
    /// ScannedNode (transient) 属性变化时同步到 row 视图。RowDetailsVisible → 切
    /// DetailsVisibility;IsOutdated → 不在此处理(操作列按钮走 binding,ButtonBase 自己
    /// 监听 PropertyChanged)。
    /// </summary>
    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ScannedNode node) return;
        if (e.PropertyName != nameof(ScannedNode.RowDetailsVisible)) return;
        // 找到对应的 DataGridRow。Nodes collection 重置后旧 row 已 Unload → 这里
        // 翻状态时它可能不在可视树,这种情况下 Load 时会重读,安全。
        foreach (var item in NodeGrid.Items)
        {
            if (!ReferenceEquals(item, node)) continue;
            if (NodeGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            {
                row.DetailsVisibility = node.RowDetailsVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            break;
        }
    }
}