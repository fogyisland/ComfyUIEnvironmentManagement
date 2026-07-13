using ComfyUI.Manager.Data;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SqliteConnectionFactory _dbFactory;

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

    public MainViewModel(SqliteConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;

        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
    }

    private void ShowEnvironments()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new EnvironmentListView
        {
            DataContext = new EnvironmentListViewModel(envRepo),
        };
    }

    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_dbFactory);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo),
        };
    }

    private void ShowSettings()
    {
        var settingsRepo = new SettingsRepository();
        CurrentView = new SettingsView
        {
            DataContext = new SettingsViewModel(settingsRepo),
        };
    }

    private void OpenBulkUpdate()
    {
        // TODO(M5.2-T6): pass a BulkUpdateOrchestrator so the dialog can drive
        // a real run. Until T6, the dialog shows placeholder messages.
        var vm = new BulkUpdateDialogViewModel();
        BulkUpdateDialog.Show(vm);
    }
}