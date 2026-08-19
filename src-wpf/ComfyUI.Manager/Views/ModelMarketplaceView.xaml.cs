using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v0.6.20 T8:模型市场 view code-behind。
/// v0.6.22 T6 加 RadioButton 源切换 click — ToggleButton kind chip + RadioButton 源 chip
/// 两种 chip 都用 Tag + Click handler 一行直接 set VM 属性,避开 IsChecked TwoWay
/// 转换 enum 难干净实现的痛点。
/// </summary>
public partial class ModelMarketplaceView : UserControl
{
    public ModelMarketplaceView()
    {
        InitializeComponent();
    }

    private void OnSourceRadioClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (rb.Tag is not ModelSourceKind kind) return;
        if (DataContext is not ModelMarketplaceViewModel vm) return;
        // 切换 radio 自动重跑当前 query(setter 触发 RefreshAsync);已选中的再点 no-op
        if (vm.ActiveSource != kind)
        {
            vm.ActiveSource = kind;
        }
    }

    private void OnKindChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        if (tb.Tag is not ModelKind kind) return;
        if (DataContext is not ModelMarketplaceViewModel vm) return;

        // 双击同一个 chip → 取消过滤
        if (vm.ActiveKindFilter == kind)
        {
            vm.ActiveKindFilter = null;
            tb.IsChecked = false;
        }
        else
        {
            vm.ActiveKindFilter = kind;
            // 视觉一致:uncheck 所有其他 chip
            UncheckOtherKindChips(tb);
        }
    }

    private void UncheckOtherKindChips(ToggleButton clicked)
    {
        if (FindName("KindFilterHost") is not ItemsControl host) return;
        foreach (var item in host.Items)
        {
            if (host.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
            {
                var chip = FindVisualChild<ToggleButton>(container);
                if (chip is not null && chip != clicked)
                {
                    chip.IsChecked = false;
                }
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
