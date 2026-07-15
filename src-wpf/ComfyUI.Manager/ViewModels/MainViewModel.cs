using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly ProcessLauncher _launcher;
    private readonly BulkUpdateOrchestrator _orchestrator;
    private readonly NodeOperations _nodeOps;
    private readonly EnvCreatorService _envCreator;

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
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator)
    {
        _dbFactory = dbFactory;
        _launcher = launcher;
        _orchestrator = orchestrator;
        _nodeOps = nodeOps;
        _envCreator = envCreator;

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
            DataContext = new EnvironmentListViewModel(envRepo, _launcher, _envCreator),
        };
    }

    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_dbFactory);
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo, envRepo, _nodeOps),
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
        var envRepo = new EnvironmentRepository(_dbFactory);
        var nodeRepo = new NodeRepository(_dbFactory);

        // 把 env 列表一次填进 EnvRows(LoadEnvs 会先 Clear),
        // 每个 EnvRow 下面挂它扫到的 node 列表(nodeId = dir name)。
        var vm = new BulkUpdateDialogViewModel(_orchestrator);
        var envRows = envRepo.ListAll().Select(env =>
        {
            var envRow = new EnvRow(env.Id, env.Name);
            foreach (var node in nodeRepo.ListByEnv(env.Id))
            {
                envRow.Nodes.Add(new NodeSelectRow(node.Id, node.Package ?? node.Id));
            }
            return envRow;
        }).ToList();
        vm.LoadEnvs(envRows);
        BulkUpdateDialog.Show(vm);
    }
}
