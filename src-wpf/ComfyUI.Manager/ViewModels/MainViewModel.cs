using System;
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
    private readonly EnvDeleterService _envDeleter;
    private readonly SettingsRepository _settingsRepo;
    private readonly GitProxyConfig _gitProxy;
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
    private readonly CatalogRefreshService _catalogRefreshService;
    private readonly CatalogCacheStore _catalogCacheStore;
    private readonly BaseEnvInstaller _baseEnvInstaller;
    private readonly BaseEnvProfileLoader _profileLoader;
    private readonly PyTorchVersionDirectory _pytorchVersionDirectory;
    private readonly string _appDataDir;
    private readonly string _projectRoot;
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly SystemInfoCollector _systemInfoCollector;

    public ErrorBannerViewModel ErrorBanner { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    // v0.6.5.20 hotfix:缓存环境页 VM/View,离开"环境"再回来时复用同一实例,
    // 这样进行中的装依赖状态(RequirementsStatus)不会随页面销毁丢失。
    // 之前每次 ShowEnvironments 都 new 一个新 VM,InstallRequirementsAsync 仍
    // 在后台跑 pip,但前台的 RequirementsStatus 已被新实例覆盖,用户回到"环境"
    // 时看到空面板,再点"装依赖"就并发触发第二次 pip。
    // Catalog / BaseEnv / Settings / SystemStatus 暂不缓存(它们是无状态的目录)。
    private EnvironmentListViewModel? _environmentsViewModel;
    private EnvironmentListView? _environmentsView;

    /// <summary>
    /// 测试 seam:构造 <see cref="EnvironmentListView"/> 的工厂 hook。
    /// 默认 new 真实 View;测试可注入 null 或 stub,绕开 WPF STA 初始化。
    /// </summary>
    internal Func<EnvironmentListViewModel, object?>? EnvironmentsViewFactory { get; set; }

    /// <summary>
    /// 测试用:获取当前缓存的"环境"页 VM(若有)。用于断言 ShowEnvironments
    /// 复用同一实例。
    /// </summary>
    internal EnvironmentListViewModel? CurrentEnvironmentsViewModel => _environmentsViewModel;

    /// <summary>
    /// 测试用:获取当前缓存的"环境"页 View 引用,验证 CurrentView 切回去时
    /// 仍是同一份(否则 XAML ContentControl 重新解析会丢绑定的状态)。
    /// </summary>
    internal object? CurrentEnvironmentsView => _environmentsView;

    public RelayCommand ShowEnvironmentsCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowBaseEnvCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenBulkUpdateCommand { get; }
    public RelayCommand ShowSystemStatusCommand { get; }

    public MainViewModel(
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator,
        EnvDeleterService envDeleter,
        SettingsRepository settingsRepo,
        GitProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogRefreshService catalogRefreshService,
        CatalogCacheStore catalogCacheStore,
        BaseEnvInstaller baseEnvInstaller,
        BaseEnvProfileLoader profileLoader,
        PyTorchVersionDirectory pytorchVersionDirectory,
        string appDataDir,
        string projectRoot,
        RequirementsInstaller requirementsInstaller,
        SystemInfoCollector systemInfoCollector)
    {
        _dbFactory = dbFactory;
        _launcher = launcher;
        _orchestrator = orchestrator;
        _nodeOps = nodeOps;
        _envCreator = envCreator;
        _envDeleter = envDeleter;
        _settingsRepo = settingsRepo;
        _gitProxy = gitProxy;
        _settings = settings;
        _catalogFetcher = catalogFetcher;
        _catalogRefreshService = catalogRefreshService;
        _catalogCacheStore = catalogCacheStore;
        _baseEnvInstaller = baseEnvInstaller;
        _profileLoader = profileLoader;
        _pytorchVersionDirectory = pytorchVersionDirectory;
        _appDataDir = appDataDir;
        _projectRoot = projectRoot;
        _requirementsInstaller = requirementsInstaller;
        _systemInfoCollector = systemInfoCollector;

        ShowEnvironmentsCommand = new RelayCommand(_ => ShowEnvironments());
        ShowCatalogCommand = new RelayCommand(_ => ShowCatalog());
        ShowBaseEnvCommand = new RelayCommand(_ => ShowBaseEnv());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());
        ShowSystemStatusCommand = new RelayCommand(_ => ShowSystemStatus());
    }

    private void ShowEnvironments()
    {
        if (_environmentsViewModel is null)
        {
            var envRepo = new EnvironmentRepository(_dbFactory);
            _environmentsViewModel = new EnvironmentListViewModel(
                envRepo, _launcher, _envCreator, _baseEnvInstaller, _settings, _profileLoader,
                _envDeleter, _nodeOps, _projectRoot, _requirementsInstaller);
            _environmentsView = EnvironmentsViewFactory is null
                ? new EnvironmentListView { DataContext = _environmentsViewModel }
                : EnvironmentsViewFactory(_environmentsViewModel) as EnvironmentListView;
        }
        CurrentView = _environmentsView;
    }

    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_catalogCacheStore);
        var versionRepo = new NodeVersionRepository(_catalogCacheStore);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo, versionRepo, _nodeOps, _catalogRefreshService, _settings, _settingsRepo, _projectRoot),
        };
    }

    private void ShowBaseEnv()
    {
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new BaseEnvView
        {
            DataContext = new BaseEnvViewModel(_profileLoader, envRepo, _baseEnvInstaller, _pytorchVersionDirectory, _appDataDir),
        };
    }

    private void ShowSettings()
    {
        CurrentView = new SettingsView
        {
            DataContext = new SettingsViewModel(_settingsRepo, _gitProxy, new PythonInterpreterValidator(), _settings),
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

    private void ShowSystemStatus()
    {
        // SystemStatusViewModel 构造时自动 RefreshAsync()(用户进 tab 立即看到数据)
        CurrentView = new SystemStatusView
        {
            DataContext = new SystemStatusViewModel(_systemInfoCollector),
        };
    }
}
