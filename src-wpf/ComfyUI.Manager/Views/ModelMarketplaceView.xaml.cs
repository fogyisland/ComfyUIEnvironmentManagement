using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v0.6.20 T8:模型市场 view code-behind。
/// 只负责 ToggleButton 过滤器 click — VM 端绑定 IsChecked TwoWay 难以干净拿到
/// "哪个 kind 被点" 的 metadata,所以用 Tag + Click handler 一行直接 set VM.ActiveKindFilter。
/// </summary>
public partial class ModelMarketplaceView : UserControl
{
    public ModelMarketplaceView()
    {
        InitializeComponent();
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
