using System.Collections.Specialized;
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>v0.6.19:工作流市场 view — mirrors BulkUpdateView Console pattern
/// (DataContextChanged hook/unhook + auto-scroll + ✕ close)。</summary>
public partial class WorkflowMarketplaceView : UserControl
{
    public WorkflowMarketplaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookConsoleLog();
        Unloaded += OnUnloaded;
    }

    private WorkflowMarketplaceViewModel? _vm;
    private NotifyCollectionChangedEventHandler? _consoleHandler;

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnhookConsoleLog();
        _vm = e.NewValue as WorkflowMarketplaceViewModel;
        HookConsoleLog();
    }

    private void HookConsoleLog()
    {
        if (_vm is null || _consoleHandler is not null) return;
        _consoleHandler = (_, _) =>
        {
            if (ConsoleScrollViewer is null) return;
            ConsoleScrollViewer.ScrollToEnd();
        };
        _vm.ConsoleLog.CollectionChanged += _consoleHandler;
    }

    private void UnhookConsoleLog()
    {
        if (_vm is null || _consoleHandler is null) return;
        _vm.ConsoleLog.CollectionChanged -= _consoleHandler;
        _consoleHandler = null;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnhookConsoleLog();
    }

    private void OnConsoleCloseClicked(object sender, System.Windows.RoutedEventArgs e)
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