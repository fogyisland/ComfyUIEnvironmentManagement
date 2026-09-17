using System.Windows.Controls;

namespace ComfyUI.Manager.Views.LocalModels;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:Thumbnails 视图 — 160x190 缩略图网格 + 标题 + Star 角标。
/// ListBox + WrapPanel + VirtualizingPanel(Pixel)。WrapPanel 不虚拟化,
/// T48 改 VirtualizingTilePanel 或自实现面板。
/// </summary>
public partial class ThumbnailsView : UserControl
{
    public ThumbnailsView()
    {
        InitializeComponent();
    }
}