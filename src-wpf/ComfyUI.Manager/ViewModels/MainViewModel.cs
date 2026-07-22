using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
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
    private readonly SettingsRepository _settingsRepo;
    private readonly GitProxyConfig _gitProxy;
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
    private readonly CatalogRefreshService _catalogRefreshService;
    private readonly CatalogCacheStore _catalogCacheStore;
    private readonly BaseEnvInstaller _baseEnvInstaller;
    private readonly BaseEnvProfileLoader _profileLoader;

    public ErrorBannerViewModel ErrorBanner { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowBaseEnvCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenBulkUpdateCommand { get; }

    public MainViewModel(
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator,
        SettingsRepository settingsRepo,
        GitProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogRefreshService catalogRefreshService,
        CatalogCacheStore catalogCacheStore,
        BaseEnvInstaller baseEnvInstaller,
        BaseEnvProfileLoader profileLoader)
    {
        _dbFactory = dbFactory;
        _launcher = launcher;
        _orchestrator = orchestrator;
        _nodeOps = nodeOps;
        _envCreator = envCreator;
        _settingsRepo = settingsRepo;
        _gitProxy = gitProxy;
        _settings = settings;
        _catalogFetcher = catalogFetcher;
        _catalogRefreshService = catalogRefreshService;
        _catalogCacheStore = catalogCacheStore;
        _baseEnvInstaller = baseEnvInstaller;
        _profileLoader = profileLoader;

        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
        ShowBaseEnvCommand = new RelayCommand(_ => ShowBaseEnv());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
    }

    private void ShowEnvironments()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new EnvironmentListView
        {
            DataContext = new EnvironmentListViewModel(envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader),
        };
    }

    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_catalogCacheStore);
        var envRepo = new EnvironmentRepository(_dbFactory);
        var versionRepo = new NodeVersionRepository(_catalogCacheStore);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo, versionRepo, envRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo),
        };
    }

    private void ShowBaseEnv()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new BaseEnvView
        {
            DataContext = new BaseEnvViewModel(_profileLoader, envRepo, _baseEnvInstaller),
        };
    }

    private void ShowSettings()
    {
        CurrentView = new SettingsView
        {
            DataContext = new SettingsViewModel(_settingsRepo, _gitProxy, _catalogRefreshService, _settings),
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
