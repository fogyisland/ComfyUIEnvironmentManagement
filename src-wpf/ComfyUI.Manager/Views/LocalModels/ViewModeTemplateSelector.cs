using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Views.LocalModels;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:ViewMode enum → 3 个 DataTemplate 路由。
/// ContentControl.ContentTemplateSelector 绑 ActiveViewMode(int)item。
/// </summary>
public sealed class ViewModeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CardsTemplate { get; set; }
    public DataTemplate? ListTemplate { get; set; }
    public DataTemplate? ThumbnailsTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item switch
        {
            ViewMode.Cards => CardsTemplate,
            ViewMode.List => ListTemplate,
            ViewMode.Thumbnails => ThumbnailsTemplate,
            _ => CardsTemplate,
        };
}

/// <summary>v1.0.0.x (2026-09-17) T47:LocalModels 视图模式枚举(T6 持久化到 model_settings)</summary>
public enum ViewMode
{
    Cards = 0,
    List = 1,
    Thumbnails = 2,
}