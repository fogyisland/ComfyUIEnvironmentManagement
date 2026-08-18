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
}