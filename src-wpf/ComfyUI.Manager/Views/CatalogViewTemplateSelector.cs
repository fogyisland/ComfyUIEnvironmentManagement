using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// CatalogViewTemplateSelector:根据 VM 的 ViewMode 选择 List 模式或 Tile 模式
/// DataTemplate。每个 ContentControl 用这个 selector 在渲染时挑 template。
/// </summary>
public class CatalogViewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ListTemplate { get; set; }
    public DataTemplate? TileTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (container is FrameworkElement fe && fe.DataContext is CatalogViewModel vm)
        {
            return vm.IsTileMode ? TileTemplate : ListTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}