using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    public ErrorBannerViewModel ErrorBanner { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenBulkUpdateCommand { get; }

    public MainViewModel()
    {
        ShowEnvironmentsCommand = new RelayCommand(_ => CurrentView = null);
        ShowCatalogCommand = new RelayCommand(_ => CurrentView = null);
        ShowSettingsCommand = new RelayCommand(_ => CurrentView = null);
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
    }

    private void OpenBulkUpdate()
    {
        // TODO(M5.2-T6): pass a BulkUpdateOrchestrator so the dialog can drive
        // a real run. Until T6, the dialog shows placeholder messages.
        var vm = new BulkUpdateDialogViewModel();
        BulkUpdateDialog.Show(vm);
    }
}
