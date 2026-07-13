using ComfyUI.Manager.Data;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly CatalogRepository _catalogRepo;
    private readonly VersionRepository _versionRepo;
    private readonly DepRepository _depRepo;
    private readonly ProcessStateRepository _processRepo;
    private readonly SettingsRepository _settingsRepo;

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

    public MainViewModel(
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        CatalogRepository catalogRepo,
        VersionRepository versionRepo,
        DepRepository depRepo,
        ProcessStateRepository processRepo,
        SettingsRepository settingsRepo)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _catalogRepo = catalogRepo;
        _versionRepo = versionRepo;
        _depRepo = depRepo;
        _processRepo = processRepo;
        _settingsRepo = settingsRepo;

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
