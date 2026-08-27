using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>v0.6.19:工作流市场 view。
/// v1.0.0.x #590:Console 面板抽取到 <see cref="ComfyUI.Manager.Controls.ConsolePanel"/>,
/// auto-scroll 跟 hook/unhook 都在 UserControl 内部。View 只剩 close handler 调 VM.ClearConsole。</summary>
public partial class WorkflowMarketplaceView : UserControl
{
    private WorkflowMarketplaceViewModel? _vm;

    public WorkflowMarketplaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => _vm = e.NewValue as WorkflowMarketplaceViewModel;
    }

    private void OnConsoleCloseRequested(object? sender, System.EventArgs e)
    {
        _vm?.ClearConsole();
    }

    /// <summary>v0.6.19.x:error banner ✕ — 清掉 VM 的 ErrorMessage,触发 IsEmpty 重算。</summary>
    private void OnErrorBannerCloseClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm?.ClearErrorMessage();
    }

    /// <summary>v0.6.19.x:info banner ✕ — 清掉 VM 的 InfoMessage。</summary>
    private void OnInfoBannerCloseClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm?.ClearInfoMessage();
    }

    /// <summary>v0.6.22 T3: mouse entered preview Border — lazy-fetch + cache workflow JSON.</summary>
    private void OnPreviewMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b
            && b.Tag is Models.WorkflowEntry entry
            && DataContext is ViewModels.WorkflowMarketplaceViewModel vm)
        {
            _ = vm.LoadJsonPreviewAsync(entry);   // fire-and-forget; per-entry cache prevents dupes
        }
    }

    /// <summary>v0.6.22 T3: mouse left preview Border — clear hover state (cache preserved).</summary>
    private void OnPreviewMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is ViewModels.WorkflowMarketplaceViewModel vm)
        {
            vm.ClearJsonOverlay();
        }
    }
}