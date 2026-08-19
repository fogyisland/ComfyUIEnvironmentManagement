using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentListViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly ProcessLauncher _launcher;
    private readonly EnvCreatorService _envCreator;
    private readonly BaseEnvInstaller _baseEnvInstaller;
    private readonly Settings _settings;
    private readonly BaseEnvProfileLoader _profileLoader;
    private readonly EnvDeleterService _envDeleter;
    private readonly NodeOperations _nodeOps;
    // v0.6.14 picker redesign:env-aware CatalogEntryPickerDialog 需要 catalogRepo +
    // nodeRepo(查 catalog 全表 + 按 env 拉 scanned_nodes)+ versionRepo(查 node_versions
    // per-row version dropdown)。null = 测试 ctor 兜底;生产 DI 在 App.xaml.cs 注入。
    private readonly CatalogRepository? _catalogRepo;
    private readonly NodeRepository? _nodeRepo;
    private readonly NodeVersionRepository? _versionRepo;
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly BaseEnvUninstaller _baseEnvUninstaller;
    private readonly RequirementsUninstaller _requirementsUninstaller;
    // v0.6.19 T10: env-start 后异步 sync workflows 到 <env.ComfyuiSource>/user/default/workflows/。
    // fire-and-forget 模式:StartEnvAsync 成功后 Task.Run 调 SyncToEnvAsync,失败仅
    // log 永远不抛(env-start status 不受 workflow 同步影响)。
    // 可空保留旧测试 ctor 兼容;生产 DI 在 App.xaml.cs 注入。
    private readonly WorkflowSymlinker? _workflowSymlinker;
    // v0.6.20 T9: env-start 后异步 sync 已下载 models 到 <env.ComfyuiSource>/models/<kind>/。
    // 同 workflow hook 模式:StartEnvAsync 成功后 Task.Run 调 SyncToEnvAsync,失败仅
    // log 永不抛(env-start status 不受 model 同步影响)。
    // 可空保留旧测试 ctor 兼容;生产 DI 在 App.xaml.cs 注入。
    private readonly ModelSymlinker? _modelSymlinker;
    // v0.6.10 T2:统一组件报告 + OpenBrowser 按钮的 Chrome 优先 fallback。
    // 可空保留测试 ctor(null! 仍能构造);生产 DI 在 App.xaml.cs 注入 new BrowserLauncher()。
    private readonly IBrowserLauncher? _browserLauncher;
    // v0.6.10 T2:BrowserLauncher 失败 → 报告给主窗口 ErrorBanner(而非 MessageBox)。
    private ErrorBannerViewModel? _errorBanner;
    private readonly string _projectRoot;

    // v0.6.11+ T3:ComfyUI Manager 装/卸 — 跟 BED install/uninstall 同样需要 per-env
    // mutex。null 兜底 new 默认实现(测试 ctor 不传也能构造;生产 DI 注入)。
    private readonly ComfyUIManagerInstaller _comfyUiManagerInstaller;
    // v0.6.22 T5:ComfyUI template update service(wipe env.ComfyuiSource 内容
    // + git clone comfyanonymous/ComfyUI --depth=1)。可空保留旧测试 ctor
    // 兼容;生产 DI 在 App.xaml.cs 注入。
    private readonly ComfyUITemplateUpdater? _templateUpdater;

    /// <summary>
    /// v0.6.5.22 T4:per-env 互斥锁 — 同 env 上同时只允许一个长操作(BED install / uninstall /
    /// requirements install / uninstall / start / stop / delete),防止并发的 BaseEnvInstaller
    /// 在末尾 upsert BedStatus="done" 复活刚被 uninstall 清空的字段。
    /// RootPath 作 key(env.Name 可能重名)。
    /// v0.6.11+ T3:加 ComfyUiManagerInstall / ComfyUiManagerUninstall 让 toggle 命令
    /// 跟其他长操作互斥(避免并发的 git clone 跟卸载冲突)。
    /// </summary>
    private enum BusyKind { None, BEDInstall, BEDUninstall, ReqInstall, ReqUninstall, Start, Stop, Delete, ComfyUiManagerInstall, ComfyUiManagerUninstall, Restart, TemplateUpdate }

    private readonly Dictionary<string, BusyKind> _envBusy = new();

    // v0.6.11+ SDD D1:MainViewModel 反向引用(打破构造期循环依赖),ctor 末尾由
    // MainViewModel.SetMainViewModel(this) 注入。null = EnvListVM 早于 MVM 构造(测试),
    // 此时 OpenInstallNodePicker 不传回调 → InstallDialog 装成功不触发重启。
    private MainViewModel? _mvm;

    // v0.6.11+ SDD D1:AppLogger — 自动重启失败 / env-not-found / busy 等诊断日志。
    // 跟 BaseEnvInstaller 同 pattern:nullable ctor,生产 DI 在 App.xaml.cs 注入。
    private readonly AppLogger? _logger;

    /// <summary>
    /// v0.6.11+ SDD D1:MainViewModel 注入反向引用。MainViewModel ctor 末尾调一次,
    /// 把 _mvm 设上,这样 OpenInstallNodePicker 才能拿 _mvm.RestartEnvAsync 当回调。
    /// </summary>
    internal void SetMainViewModel(MainViewModel mvm) => _mvm = mvm;

    /// <summary>
    /// v0.6.11+ SDD D1 (test seam):测试拦截 StartEnvAsync 调用 — ProcessLauncher
    /// 是 sealed 不可继承,这里加 Func delegate field,设了就代替 _launcher.StartEnvAsync
    /// 被调,否则走默认 _launcher.StartEnvAsync。
    /// </summary>
    internal Func<
        Environment,
        IProgress<string>?,
        IProgress<string>?,
        CancellationToken,
        Task>? StartEnvForTest { get; set; }

    /// <summary>
    /// v0.6.11+ SDD D1 (test seam):测试拦截 StopEnvAsync 调用 — 同 StartEnvForTest,
    /// 设了代替 _launcher.StopEnvAsync 被调,默认走默认 _launcher.StopEnvAsync。
    /// </summary>
    internal Func<Environment, Task>? StopEnvForTest { get; set; }

    /// <summary>
    /// v0.6.5.22 fix-wave:卸载依赖 CancellationTokenSource — 跨 invocation 重建,
    /// finally 里 Dispose + null,避免上一个 invocation 的 CTS 留在 status VM 里让
    /// CancelCommand.CanExecute 误返 true 但实际 Cancel 无效(按钮绑了也不通)。
    /// </summary>
    private CancellationTokenSource? _uninstallCts;

    private bool IsEnvBusy(Environment env)
        => env is not null && _envBusy.ContainsKey(env.RootPath);

    private void MarkEnvBusy(Environment env, BusyKind kind)
        => _envBusy[env.RootPath] = kind;

    private void UnmarkEnvBusy(Environment env)
        => _envBusy.Remove(env.RootPath);

    /// <summary>
    /// Test seam:手动 mark env 为 busy(模拟其他 long-running 操作占用)。
    /// 测试用 — 让 Toggle 命令 CanExecute 验 false 而不依赖其他 fixture 副作用。
    /// 生产代码不需要这个,直接从 IsEnvBusy 走。
    /// </summary>
    internal void SetEnvBusyForTest(Environment env)
    {
        if (env is null) return;
        MarkEnvBusy(env, BusyKind.ReqInstall);
    }

    public ObservableCollection<Environment> Environments { get; } = new();

    // v0.6.15.8 T5:per-env VM cache — 切换 env 不重建,保留 selected row / scroll /
    // 弹窗状态。Cache hit 不会触发 ScanAsync/LoadAsync(那些走 ctor 一次性初始化)。
    private readonly Dictionary<string, NodeManagementViewModel> _nodeMgmtCache = new();
    private NodeManagementViewModel? _nodeManagement;

    /// <summary>
    /// v0.6.15.8 T5:当前显示的 NodeManagement VM。null → 面板隐藏;
    /// non-null → 面板可见(<see cref="IsNodeManagementVisible"/>)。Setter 触发
    /// IsNodeManagementVisible 通知让 XAML 切换面板 Visibility。
    /// v0.6.15.9:升级功能迁入 NodeManagement 面板(行内 升级 按钮),不再需要独立的
    /// UpgradeNodes VM / 面板 / 命令。
    /// </summary>
    public NodeManagementViewModel? NodeManagement
    {
        get => _nodeManagement;
        private set
        {
            if (SetField(ref _nodeManagement, value))
                RaisePropertyChanged(nameof(IsNodeManagementVisible));
        }
    }
    public bool IsNodeManagementVisible => _nodeManagement is not null;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand BaseEnvCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand InstallNodeCommand { get; }
    public RelayCommand InstallRequirementsCommand { get; }
    public RelayCommand UninstallBaseEnvCommand { get; }
    public RelayCommand UninstallRequirementsCommand { get; }
    public RelayCommand ReportComponentsCommand { get; }
    public RelayCommand OpenBrowserCommand { get; }
    /// <summary>
    /// v0.6.22 T4:env-list Row 0 col 2 新增"进入虚拟环境"图标按钮(在 ⌨ 旁边)。
    /// 参数 = Environment;CanExecute 要求 env.VenvPath 非空且目录存在(避免已删 env
    /// 点图标静默失败)。Execute 调 <see cref="OpenVenv"/> 启动 cmd.exe /k cd 到 venv。
    /// </summary>
    public RelayCommand OpenVenvCommand { get; }
    /// <summary>
    /// v0.6.11+ T3:env-list 行 6th 按钮 "装/卸 ComfyUI Manager" toggle 命令 —
    /// 根据 IsComfyUiManagerInstalled 切换 Install / Uninstall,inline 状态面板显示进度。
    /// </summary>
    public RelayCommand ToggleComfyUiManagerCommand { get; }

    /// <summary>
    /// v0.6.11+ T1:env-list 行 toggle "装依赖/卸依赖" 命令 — 根据
    /// IsRequirementsInstalled 切换 Install / Uninstall。复用现有
    /// InstallRequirementsAsync / UninstallRequirementsAsync 子命令。
    /// </summary>
    public RelayCommand ToggleRequirementsCommand { get; }

    /// <summary>
    /// v0.6.11+ T1:env-list 行 toggle "安装基础环境/卸载基础环境" 命令 — 根据
    /// IsBaseEnvInstalled 切换 Install (走 picker dialog) / Uninstall。
    /// </summary>
    /// <summary>
    /// v0.6.15.8 T5:env-list 行 "管理节点" 按钮 — 弹 NodeManagement 底部面板
    /// (per-env cached VM,关闭后切回仍保留状态)。
    /// </summary>
    public RelayCommand OpenNodeManagementCommand { get; }

    /// <summary>
    /// v0.6.15.8 T5:NodeManagement 面板 ✕ 按钮 — 清空当前显示(VM 留在 cache 里
    /// 备 re-open 用)。
    /// </summary>
    public RelayCommand CloseNodeManagementCommand { get; }

    /// <summary>
    /// v0.6.22 T5:env-list 行"模板更新"按钮 — 删除 env.ComfyuiSource 全部内容
    /// 后 git clone comfyanonymous/ComfyUI --depth=1。destructive,会先弹
    /// MessageBox 确认。CanExecute:env 不在 busy + _templateUpdater 已注入 +
    /// env.ComfyuiSource 路径存在。
    /// </summary>
    public RelayCommand UpdateTemplateCommand { get; }

    public RelayCommand ToggleBaseEnvCommand { get; }

    public string? RecentBasePythonPath { get; private set; }

    /// <summary>
    /// Test seam — unit tests set this to intercept the confirmation dialog (which would
    /// call MessageBox.Show and hang in test context). Returns true = user confirmed, false = cancelled.
    /// </summary>
    public Func<Environment, bool>? ConfirmDeleteOverride { get; set; }

    /// <summary>
    /// v0.6.5.22 T4:卸载二次确认 dialog seam。生产路径默认走 MessageBox(VM 留 hook
    /// 给后续 commit 接更完整的 dialog);测试用 Func&lt;string,string,bool&gt; 拦截
    /// (message, title) → 返回 true = 确认, false = 取消。
    /// </summary>
    public Func<string, string, bool>? ShowConfirmDialogOverride { get; set; }

    /// <summary>
    /// v0.6.5.22 T4:卸载/拒绝场景下的信息提示 dialog seam(message, reason)。
    /// </summary>
    public Action<string, string>? ShowMessageBoxOverride { get; set; }

    /// <summary>
    /// v0.6.7 T2:组件报告 builder seam。null = 生产路径,按 _profileLoader / _repo /
    /// _settings.GitExe / 程序集版本 现造一个。测试注入子类伪造 BuildAsync。
    /// </summary>
    public EnvComponentReportBuilder? ComponentReportBuilderOverride { get; set; }

    /// <summary>
    /// v0.6.7 T2:打开生成的 HTML seam。生产路径走 Process.Start(UseShellExecute=true)
    /// 交给默认浏览器;测试拦下来只记录路径,避免真的弹浏览器。
    /// </summary>
    public Action<string>? OpenReportFileOverride { get; set; }

    /// <summary>
    /// v0.6.7.2:打开 ComfyUI 页面的 seam(参数 = 完整 URL)。生产路径优先用 Chrome,
    /// 找不到 Chrome 则回退系统默认浏览器;测试拦下来只记录 URL。
    /// </summary>
    public Action<string>? OpenBrowserUrlOverride { get; set; }

    /// <summary>
    /// v0.6.10 T2:BrowserLauncher 测试 seam — 完全替换注入的 IBrowserLauncher,
    /// 用于将来禁用 Chrome 测试或断言 launcher 真的被调。null = 走 _browserLauncher。
    /// </summary>
    public IBrowserLauncher? BrowserLauncherOverride { get; set; }

    /// <summary>
    /// BED 卸载 inline 状态面板(env-list 操作列"卸载基础环境"按钮触发后)。单 VM,
    /// 跟 RequirementsStatusViewModel 同模式 — 完成 → 2s 自动 Hide;失败 → 等用户关。
    /// </summary>
    public BaseEnvUninstallStatusViewModel? BaseEnvUninstallStatus { get; private set; }

    /// <summary>
    /// v0.6.11+ T3:ComfyUI Manager 装/卸 inline 状态面板(env-list 操作列 toggle
    /// 按钮触发后)。镜像 <see cref="RequirementsStatusViewModel"/> 单阶段模式。
    /// </summary>
    public ComfyUIManagerStatusViewModel? ComfyUiManagerStatus { get; private set; }

    /// <summary>
    /// v0.6.22 T5:ComfyUI 模板更新 inline 状态面板(env-list 操作列"模板更新"
    /// 按钮触发后)。镜像 <see cref="RequirementsStatusViewModel"/> 单阶段模式:
    /// 3-state IsVisible (!userHidden && (IsBusy || HasContent || HasError)),
    /// ✕ 按钮由 <see cref="OnTemplateUpdateStatusCloseClicked"/> 调 Clear()。
    /// </summary>
    public TemplateUpdateStatusViewModel TemplateUpdateStatus { get; } = new();

    public EnvironmentListViewModel(
        EnvironmentRepository repo,
        ProcessLauncher launcher,
        EnvCreatorService envCreator,
        BaseEnvInstaller baseEnvInstaller,
        Settings settings,
        BaseEnvProfileLoader profileLoader,
        EnvDeleterService envDeleter,
        NodeOperations nodeOps,
        string projectRoot,
        RequirementsInstaller requirementsInstaller,
        BaseEnvUninstaller? baseEnvUninstaller = null,
        RequirementsUninstaller? requirementsUninstaller = null,
        IBrowserLauncher? browserLauncher = null,
        ErrorBannerViewModel? errorBanner = null,
        ComfyUIManagerInstaller? comfyUiManagerInstaller = null,
        AppLogger? logger = null,
        CatalogRepository? catalogRepo = null,
        NodeRepository? nodeRepo = null,
        NodeVersionRepository? versionRepo = null,
        // v0.6.19 T10: env-start 后异步 sync 已下载 workflows 到 env。可空保留
        // 旧测试 ctor 兼容;生产 DI 在 App.xaml.cs 注入。
        WorkflowSymlinker? workflowSymlinker = null,
        // v0.6.20 T9: env-start 后异步 sync 已下载 models 到 env。可空保留旧测试 ctor 兼容;
        // 生产 DI 在 App.xaml.cs 注入。Signature(envId, envComfyuiSource, ct) — envId
        // 取 env.Id, envComfyuiSource 取 env.ComfyuiSource(同 workflow hook)。
        ModelSymlinker? modelSymlinker = null,
        // v0.6.22 T5:ComfyUI 模板更新 service(wipe + git clone)。可空保留
        // 旧测试 ctor 兼容;生产 DI 在 App.xaml.cs 注入。null 时
        // UpdateTemplateCommand.CanExecute 永远 false(按钮 disabled)。
        ComfyUITemplateUpdater? templateUpdater = null)
    {
        _repo = repo;
        _launcher = launcher;
        _envCreator = envCreator;
        _baseEnvInstaller = baseEnvInstaller;
        _settings = settings;
        _profileLoader = profileLoader;
        _envDeleter = envDeleter;
        _nodeOps = nodeOps;
        _projectRoot = projectRoot;
        _requirementsInstaller = requirementsInstaller;
        _baseEnvUninstaller = baseEnvUninstaller ?? new BaseEnvUninstaller();
        _requirementsUninstaller = requirementsUninstaller ?? new RequirementsUninstaller();
        // v0.6.10 T2:BrowserLauncher + ErrorBanner 默认 null,保留现有测试 ctor 调用;
        // 生产 DI 在 App.xaml.cs 注入(new BrowserLauncher() + mainVm.ErrorBanner)。
        _browserLauncher = browserLauncher;
        _errorBanner = errorBanner;
        // v0.6.11+ T3:默认 new 一个 fallback 实例(让测试 ctor 不传也能构造);生产
        // DI 在 App.xaml.cs 注入 shareComfyUiManagerInstaller。
        _comfyUiManagerInstaller = comfyUiManagerInstaller ?? new ComfyUIManagerInstaller(new RequirementsFileInstaller());
        // v0.6.11+ SDD D1:AppLogger — 自动重启诊断日志(nullable ctor param)。
        _logger = logger;
        // v0.6.14 picker redesign:catalog + node + version repo(默认 null,测试 ctor
        // 不传也能构造)。生产 DI 在 App.xaml.cs 注入。null 时 OpenInstallNodePicker 走
        // CatalogPickerOverride / fallback short-circuit(详见方法体)。
        _catalogRepo = catalogRepo;
        _nodeRepo = nodeRepo;
        _versionRepo = versionRepo;
        // v0.6.19 T10: env-start hook — fire-and-forget sync workflows 到 env。
        _workflowSymlinker = workflowSymlinker;
        // v0.6.20 T9: env-start hook — fire-and-forget sync models 到 env。
        _modelSymlinker = modelSymlinker;
        // v0.6.22 T5:ComfyUI 模板更新 service — UpdateTemplateCommand.CanExecute
        // 依赖它非 null。
        _templateUpdater = templateUpdater;
        RecentBasePythonPath = null;
        RefreshCommand = new RelayCommand(_ => Load());
        StartCommand = new RelayCommand(
            async p => await StartEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (env.Status != "stopped") return false;
                // BED 未装 / 装中 → 禁用
                if (env.BedStatus is null or "installing") return false;
                // per-env mutex:同 env 上已有 BED install/uninstall/req/start/stop/delete 在跑 → 禁用
                if (IsEnvBusy(env)) return false;
                return true;
            });
        StopCommand = new RelayCommand(
            async p => await StopEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env?.Status != "running") return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        ShowLogCommand = new RelayCommand(
            p => ShowLog(p as Environment ?? Selected),
            p => (p as Environment ?? Selected)?.Status == "running");
        OpenBrowserCommand = new RelayCommand(
            p => OpenBrowser(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                // 只有跑起来且知道端口才有页面可开。
                return env is { Status: "running" } && env.Port.HasValue;
            });
        // v0.6.22 T4:env-list Row 0 col 2 新增"进入虚拟环境"图标按钮。
        // CanExecute = VenvPath 非空且目录存在(避免已删 env 点了图标静默失败)。
        OpenVenvCommand = new RelayCommand(
            p => OpenVenv(p as Environment),
            p => p is Environment e && !string.IsNullOrWhiteSpace(e.VenvPath) && Directory.Exists(e.VenvPath));
        CreateCommand = new RelayCommand(_ => CreateEnv());
        BaseEnvCommand = new RelayCommand(
            async _ => await OpenBaseEnvProgressAsync(),
            _ =>
            {
                if (Environments.Count == 0) return false;
                // 工具栏 BED 入口:若 Selected 已 busy → 禁用;无 Selected 时检查
                // 是否全部 env 都空闲(避免并发触发多个 BED install)。
                if (Selected is not null) return !IsEnvBusy(Selected);
                return Environments.All(e => !IsEnvBusy(e));
            });
        DeleteCommand = new RelayCommand(
            async p => await DeleteEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        InstallNodeCommand = new RelayCommand(
            p => OpenInstallNodePicker(p as Environment ?? Selected),
            p => (p as Environment ?? Selected) is not null);
        InstallRequirementsCommand = new RelayCommand(
            async p => await InstallRequirementsAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        UninstallBaseEnvCommand = new RelayCommand(
            async p => await UninstallBaseEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (!BaseEnvUninstaller.IsInstalled(env)) return false;
                if (env.Status == "running") return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        UninstallRequirementsCommand = new RelayCommand(
            async p => await UninstallRequirementsAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (!RequirementsInstaller.IsInstalled(env)) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        ReportComponentsCommand = new RelayCommand(
            p => ReportComponentsExecuteWrapper(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                return !IsEnvBusy(env);
            });
        // v0.6.11+ T3:ComfyUI Manager toggle 命令 — 根据 IsComfyUiManagerInstalled
        // 切换 Install / Uninstall;失败 → 面板持续可见等用户关,成功 → 2s 自动 Hide。
        ToggleComfyUiManagerCommand = new RelayCommand(
            async p => await ToggleComfyUiManagerAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        // v0.6.11+ T1:Requirements + BED toggle 命令 — 同 ComfyUI Manager toggle
        // 模式。CanExecute 只看 IsEnvBusy(没装/已装判定让 toggle 方法自己走)。
        ToggleRequirementsCommand = new RelayCommand(
            async p => await ToggleRequirementsAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        ToggleBaseEnvCommand = new RelayCommand(
            async p => await ToggleBaseEnvAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        // v0.6.22 T5:env-list 行"模板更新"按钮 — destructive(wipe + reclone)。
        // CanExecute:env 不在 busy + _templateUpdater 已注入 + env.ComfyuiSource
        // 路径存在(否则 wipe 立即 fail 没意义)。Confirm gate 走 MessageBox
        // 由 Execute 内调 ConfirmDangerous,CanExecute 只看前提条件。
        UpdateTemplateCommand = new RelayCommand(
            async p => await UpdateTemplateAsync(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                if (_templateUpdater is null) return false;
                if (string.IsNullOrWhiteSpace(env.ComfyuiSource)) return false;
                if (!Directory.Exists(env.ComfyuiSource)) return false;
                return true;
            });
        // v0.6.15.8 T5:NodeManagement 面板 open/close 命令。
        // CanExecute 镜像其他长操作命令(看 env 非 null + !IsEnvBusy)。
        // Close 命令始终可执行(允许用户在面板可见时手动关)。
        // v0.6.15.9:删 OpenUpgradeNodesCommand + CloseUpgradeNodesCommand,升级功能
        // 迁入 NodeManagement 面板(行内按钮)。
        OpenNodeManagementCommand = new RelayCommand(
            p => OpenNodeManagement(p as Environment ?? Selected),
            p =>
            {
                var env = p as Environment ?? Selected;
                if (env is null) return false;
                if (IsEnvBusy(env)) return false;
                return true;
            });
        CloseNodeManagementCommand = new RelayCommand(_ => NodeManagement = null);
        // v0.6.17:env 行 "再次打开" 启动日志按钮 — 取出缓存的 VM 设回 StartStatus。
        // v0.6.17.1:CanExecute 只看 env 参数是否非 null(常驻按钮 — 即使 env 从未
        // 启动过也允许点,Execute 内部检查 dict,没有条目就静默 no-op)。env 行
        // 图标永远亮起,通过 brush 颜色告诉用户面板是否打开。
        ReopenStartStatusCommand = new RelayCommand(
            p => ReopenStartStatus(p as Environment ?? Selected),
            p => (p as Environment ?? Selected) is not null);
        Load();
    }

    // v0.6.15.7 T7 dead code removed in v0.6.15.8 T5:env-detail right-side panel
    // 整体被 XAML 改 bottom-popup 替代(详见 EnvironmentListView XAML 重构 T6)。
    // _environmentDetail / _environmentDetailEnvId / EnvironmentDetail /
    // HasEnvironmentDetail / SelectedChangedHandler 全部删除。

    private Environment? _selected;
    public Environment? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RaisePropertyChanged(nameof(StartTooltip));
            }
        }
    }

    /// <summary>
    /// v0.6.9 T7:Spotlight 选中 Environment target 后,MainViewModel 调这里把
    /// DataGrid.Selected 切到对应 env。XAML 双向 binding 自动滚动到可见行。
    /// </summary>
    public void SelectEnvironment(string envId)
    {
        var env = Environments.FirstOrDefault(e => e.Id == envId);
        if (env is null) return;
        Selected = env;
    }

    /// <summary>
    /// 启动按钮 tooltip 文本:基于 Selected env 的 BED 状态。
    /// - BedStatus null   → "基础环境未安装"
    /// - BedStatus "installing" → "基础环境安装中,请稍候"
    /// - BedStatus "failed" → "上次基础环境部署失败:{BedFailedReason};运行可能也失败"
    /// - BedStatus "done"  → ""(BED OK,不需要提示)
    /// - env is null       → ""
    /// </summary>
    public string StartTooltip
    {
        get
        {
            var env = Selected;
            if (env is null) return "";
            return env.BedStatus switch
            {
                null => "基础环境未安装",
                "installing" => "基础环境安装中,请稍候",
                "failed" => $"上次基础环境部署失败:{env.BedFailedReason};运行可能也失败",
                _ => "",
            };
        }
    }

    private void Load()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
        RecomputeRecentBasePythonPath();
        // v0.6.11+ T3:计算每行 ComfyUI Manager 装态 + 按钮文字。
        // 不持久化(Environment.IsComfyUiManagerInstalled 是 JsonIgnore),Load 末尾
        // 重算避免 stale;toggle 命令 CanExecute 也因此依赖 Load 后状态。
        // v0.6.11+ T1:同步计算 Requirements (marker 文件) + BED (BedStatus) toggle 状态,
        // 让 toggle 命令按钮文字同步反映当前装态。
        foreach (var env in Environments)
        {
            var installed = _comfyUiManagerInstaller.IsInstalled(env);
            env.IsComfyUiManagerInstalled = installed;
            env.ComfyUiManagerButtonText = installed ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";

            var reqInstalled = RequirementsInstaller.IsInstalled(env);
            env.IsRequirementsInstalled = reqInstalled;
            env.RequirementsButtonText = reqInstalled ? "卸依赖" : "装依赖";

            var bedInstalled = BaseEnvUninstaller.IsInstalled(env);
            env.IsBaseEnvInstalled = bedInstalled;
            env.BaseEnvButtonText = bedInstalled ? "卸载基础环境" : "安装基础环境";
        }
        RaiseCommandsChanged();
    }

    private void RecomputeRecentBasePythonPath()
    {
        if (Environments.Count == 0)
        {
            RecentBasePythonPath = null;
            return;
        }
        // Pick the env with the latest RootPath mtime (when the directory exists),
        // falling back to descending Id lexicographic order when no mtime is available.
        var latest = Environments
            .OrderByDescending(e =>
            {
                try
                {
                    return Directory.Exists(e.RootPath)
                        ? new DirectoryInfo(e.RootPath).LastWriteTimeUtc.Ticks
                        : 0;
                }
                catch { return 0; }
            })
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
        RecentBasePythonPath = latest?.BasePythonPath;
    }

    private async System.Threading.Tasks.Task StartEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;  // T4:per-env mutex
        var status = new EnvStartStatusViewModel { Title = $"启动状态 — {env.Name}" };
        // v0.6.17:per-env VM cache — 启动结束后面板留着(用户可手动 ✕ 关),后续
        // "再次打开" 命令从 dict 拿回原 VM 显示。重复启动同一 env 替换 dict 条目。
        _startStatuses[env.Id] = status;
        SetStartStatus(status, env.Id);
        status.Begin();
        MarkEnvBusy(env, BusyKind.Start);
        // 把 status 包成 Progress<string>:Progress<T> 构造时捕获当前
        // SynchronizationContext(UI 线程),Report 回调自动 marshal 回 UI 线程。
        // ProcessLauncher.AttachStdoutReader / AttachStderrReader 跑在 Task.Run
        // 后台线程,直接传 status 会在后台线程改 LogLines ObservableCollection,
        // 触发 WPF "某个 itemscontrol 与它的项源不一致"。
        var stageProgress = new Progress<string>(s => status.Report(s));
        var logProgress = new Progress<string>(line => status.Report(line));
        try
        {
            if (StartEnvForTest is not null)
            {
                await StartEnvForTest(env, stageProgress, logProgress, default);
            }
            else
            {
                await _launcher.StartEnvAsync(env, stageProgress, logProgress, default);
            }
            status.Complete();
            // v0.6.17:不再 auto-hide — 面板留着让用户手动关 ✕;后续 "再次打开" 按钮
            // 直接复用 dict[env.Id] 显示同一 VM(数据不丢)。

            // v0.6.19 T10: env-start 成功后 fire-and-forget sync workflows 到 env
            // 的 user/default/workflows/。失败仅 log 永不抛 — workflow 同步失败
            // 不阻断 env-start status,用户看到 running 即可。
            // 走 Task.Run 而不是直接 await 是因为 JunctionLinker 跟 I/O 可能在
            // 网络盘 / 大目录上花几秒,我们不想延长 env-start 反馈面板的 Complete 时刻。
            if (_workflowSymlinker is not null && !string.IsNullOrEmpty(env.ComfyuiSource))
            {
                var symlinker = _workflowSymlinker;
                var comfyuiSource = env.ComfyuiSource;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await symlinker.SyncToEnvAsync(comfyuiSource).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("workflow-symlink",
                            $"fire-and-forget sync failed: {ex.Message}");
                    }
                });
            }

            // v0.6.20 T9: env-start 成功后 fire-and-forget sync 已下载 models 到 env
            // 的 models/<kind>/<model-slug>-<id8>__<version-slug>-<vid8>。失败仅 log
            // 永不抛 — model 同步失败不阻断 env-start status,workflow + model 两个
            // hook 互不干扰,各自错误隔离。SyncToEnvAsync 内部 per-version try/catch
            // + Errors list 聚合(对最终用户看不到),这里 catch 兜底防漏网异常。
            if (_modelSymlinker is not null && !string.IsNullOrEmpty(env.ComfyuiSource))
            {
                var modelSymlinker = _modelSymlinker;
                var envId = env.Id;
                var comfyuiSource = env.ComfyuiSource;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await modelSymlinker.SyncToEnvAsync(comfyuiSource).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("model-symlink",
                            $"env '{envId}' fire-and-forget sync failed: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            status.Fail($"启动失败:{ex.Message}");
            // 不收起,等用户手动关 — 用户能看到错误
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// v0.6.17:per-env 启动状态缓存(支持 env 跑起来后"再次打开"启动日志)。
    /// 每个 env 启动一次都建一个独立 <see cref="EnvStartStatusViewModel"/>,放在
    /// 这里;UI 关掉面板(<see cref="StartStatus"/> = null)不会清条目,后续 reopen
    /// 直接拿回原 VM。同一 env 重复启动会替换 dict 条目。
    /// </summary>
    private readonly Dictionary<string, EnvStartStatusViewModel> _startStatuses = new();

    private EnvStartStatusViewModel? _startStatus;
    public EnvStartStatusViewModel? StartStatus
    {
        get => _startStatus;
        private set
        {
            _startStatus = value;
            RaisePropertyChanged(nameof(StartStatus));
        }
    }

    /// <summary>
    /// v0.6.17.1:当前 StartStatus 显示的是哪个 env 的面板。null = 没面板。
    /// env 行的 🪵 图标用这个值切深/浅色(深色 = 该 env 面板正打开)。
    /// </summary>
    public string? ActiveStartStatusEnvId { get; private set; }

    /// <summary>
    /// v0.6.17:env 跑起来后"再次打开启动日志"命令 — 把对应 env 的 VM 设回
    /// <see cref="StartStatus"/> 让面板重新可见。v0.6.17.1:CanExecute 只看 env
    /// 参数是否非 null(常驻按钮 — 即使 env 从未启动过也允许点,Execute 内部
    /// 检查 dict,没有条目就静默 no-op)。测试 / 面板 ✕ 按钮通过
    /// <see cref="CloseStartStatusPanel"/> 隐面板,dict 条目留着。
    /// </summary>
    public RelayCommand ReopenStartStatusCommand { get; }

    /// <summary>
    /// 内部 helper — 同时更新 <see cref="StartStatus"/> 和
    /// <see cref="ActiveStartStatusEnvId"/>,保证两属性同步。
    /// </summary>
    private void SetStartStatus(EnvStartStatusViewModel? vm, string? envId)
    {
        _startStatus = vm;
        ActiveStartStatusEnvId = envId;
        RaisePropertyChanged(nameof(StartStatus));
        RaisePropertyChanged(nameof(ActiveStartStatusEnvId));
    }

    /// <summary>
    /// v0.6.17:面板 ✕ 按钮 handler — 把 <see cref="StartStatus"/> 置 null 让面板
    /// 隐藏;dict 条目不动,用户随时可通过 ReopenStartStatusCommand 重新打开。
    /// </summary>
    public void CloseStartStatusPanel()
    {
        SetStartStatus(null, null);
    }

    private void ReopenStartStatus(Environment? env)
    {
        if (env is null) return;
        if (!_startStatuses.TryGetValue(env.Id, out var status))
        {
            // v0.6.17.1:env 本会话没记录 → 区分两种场景,避免统一报"未启动"
            // 让用户困惑(env 实际可能在跑,只是 manager 重启前的运行实例)。
            //
            // (a) env.Status == "running" 但本会话没启动过:典型场景 = manager
            //     重启后 _startStatuses dict 空了(内存丢),env 进程还活着。
            //     提示用 env 行「查看日志」按钮看实时 stdout。
            // (b) env.Status == "stopped" 也没启动过:真没日志,引导先点启动。
            var running = string.Equals(env.Status, "running", StringComparison.OrdinalIgnoreCase);
            var msg = running
                ? $"env '{env.Name}' 当前正在运行,但本会话没有捕获到它的启动日志。\n" +
                  "可能原因:manager 重启前的运行实例,或者手动用其他工具启动。\n" +
                  "本图标只记录当前会话的启动过程。要查看实时输出请用 env 行的「查看日志」按钮。"
                : $"env '{env.Name}' 还未启动,没有启动日志可查看。\n" +
                  "请先点 env 行的「启动」按钮,启动后面板会保留在此图标上。";
            ShowInfoDialog(msg, "启动控制台");
            return;
        }
        SetStartStatus(status, env.Id);
    }

    /// <summary>
    /// v0.6.11+ SDD D1:给 MainViewModel.RestartEnvAsync 调的内部入口。
    /// Stop(若 env.Status == "running")+ Start,复用 per-env 互斥锁 + EnvStartStatusViewModel。
    /// 失败 → AppLogger + env-start 面板 Fail(rethrow no,节点保留)。
    /// 跳过条件:env 找不到 / env 已在 busy 状态(per-env 互斥锁,v0.6.5.22)。
    ///
    /// ProcessLauncher sealed 测试隔离:StartEnvForTest / StopEnvForTest 设了就代替
    /// _launcher 被调(同 InstallDialogViewModelTests 用 Func delegate seam 替代 sealed
    /// 依赖的 pattern);默认走 _launcher。
    /// </summary>
    internal async Task RestartEnvInternalAsync(Environment env, CancellationToken ct)
    {
        if (env is null)
        {
            _logger?.Warn("auto-restart-env", "env 为 null,跳过重启");
            return;
        }
        if (IsEnvBusy(env))
        {
            _logger?.Warn("auto-restart-env-busy",
                $"env {env.Name} 正忙,跳过自动重启");
            return;
        }

        // 跟 StartEnvAsync 一样构造 status panel,复用现有 EnvStartStatusViewModel 显示
        var status = new EnvStartStatusViewModel { Title = $"启动状态 — {env.Name}" };
        // v0.6.17:per-env VM cache — 同 StartEnvAsync 一起用 _startStatuses 字典,
        // 跑起来后面板留着让用户 reopen。
        _startStatuses[env.Id] = status;
        SetStartStatus(status, env.Id);
        status.Begin();
        MarkEnvBusy(env, BusyKind.Restart);
        // v0.6.5.11 fix:把 status 包成 Progress<string> 捕获 UI SynchronizationContext,
        // 避免 AttachStdoutReader 后台线程改 LogLines ObservableCollection。
        var stageProgress = new Progress<string>(s => status.Report(s));
        var logProgress = new Progress<string>(line => status.Report(line));

        try
        {
            // 1) Stop if running — test seam 优先,默认走 _launcher
            if (string.Equals(env.Status, "running", StringComparison.Ordinal))
            {
                if (StopEnvForTest is not null)
                {
                    await StopEnvForTest(env);
                }
                else
                {
                    await _launcher.StopEnvAsync(env);
                }
            }

            // 2) Start — test seam 优先,默认走 _launcher
            if (StartEnvForTest is not null)
            {
                await StartEnvForTest(env, stageProgress, logProgress, default);
            }
            else
            {
                await _launcher.StartEnvAsync(env, stageProgress, logProgress, default);
            }
            status.Complete();
            // v0.6.17:不再 auto-hide — 同 StartEnvAsync,面板留着让用户 ✕ 关。
        }
        catch (Exception ex)
        {
            status.Fail($"自动重启失败:{ex.Message}");
            _logger?.Error("auto-restart-env-failed",
                $"env {env.Name} 自动重启失败(节点保留):{ex.Message}", ex);
            // 不抛 — InstallDialogViewModel 已经在 background 跑,异常会丢失
            // AppLogger 已记录,env-start 面板显示用户可见错
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    private async System.Threading.Tasks.Task StopEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;  // T4:per-env mutex
        MarkEnvBusy(env, BusyKind.Stop);
        try
        {
            await _launcher.StopEnvAsync(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"停止 env '{env.Name}' 失败:\n{ex.Message}",
                "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    private void ShowLog(Environment? env)
    {
        if (env is null) return;
        var logPath = _launcher.LogFilePath(env.Name, env.Id);
        // v0.6.12: 第一参数其实只是窗口 Title 显示用,这里改传 env.Name(用户友好)。
        LogViewerDialog.Show(env.Name, logPath);
    }

    private void CreateEnv()
    {
        var created = Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, RecentBasePythonPath, _repo);
        if (created is not null) Load();
    }

    // Test seam — unit tests set this to intercept the dialog launch (which calls
    // Application.Current.MainWindow + ShowDialog() and would throw in test context).
    public Action<IReadOnlyList<string>, BaseEnvProfile, BaseEnvInstaller>? ShowProgressDialogOverride { get; set; }

    /// <summary>
    /// v0.6.6 测试 seam:覆盖 <see cref="BaseEnvProfilePickerDialog.Show"/>。不设走真实 WPF dialog。
    /// Func(profiles, preselected, mode) → 选中 list 或 null(取消)。
    /// </summary>
    public Func<
        IReadOnlyList<BaseEnvProfile>,
        BaseEnvProfile?,
        PickerSelectionMode,
        IReadOnlyList<BaseEnvProfile>?>? PickerDialogOverride { get; set; }

    /// <summary>
    /// 测试 seam:生产代码弹 MessageBox("已安装" 提示),单测可赋值 trap 避免 STA 挂死。
    /// v0.6.5.19.1 hotfix 加。
    /// </summary>
    public Action<string>? MessageBoxOverride { get; set; }

    private async Task OpenBaseEnvProgressAsync(Environment? targetEnv = null)
    {
        // v0.6.11+ T1:per-env toggle 入口 — 只对这个 env 弹 picker + progress dialog,
        // 装完直接更新 IsBaseEnvInstalled / BaseEnvButtonText 让 toggle 切到"卸载"。
        // null = 工具栏 BaseEnvCommand 入口,用 Selected 或全 env(原行为不变)。
        if (targetEnv is not null)
        {
            await OpenBaseEnvProgressForSingleEnvAsync(targetEnv);
            return;
        }
        if (Selected is null && Environments.Count == 0) return;
        var envIds = Selected is not null
            ? new List<string> { Selected.Id }
            : Environments.Select(e => e.Id).ToList();
        if (envIds.Count == 0) return;

        // v0.6.5.19.1 hotfix: env-list 工具栏"基础环境部署"按钮也加 all-done 短路 —
        // v0.6.5.19 只修了 BaseEnv tab 的 StartCommand,这个入口漏修。BedStatus 全部
        // "done" → 弹"已安装",不弹 install dialog,跟 v0.6.5.19 BED 入口短路行为一致。
        var existingEnvs = envIds
            .Select(id => _repo.Get(id))
            .Where(e => e is not null)
            .ToList();
        if (existingEnvs.Count == envIds.Count
            && existingEnvs.All(e => e!.BedStatus == "done"))
        {
            var names = string.Join(", ", existingEnvs.Select(e => e!.Name));
            ShowAlreadyInstalled(
                $"所选 env 已安装基础环境,无需再装:{names}");
            return;
        }

        // v0.6.5.22 fix-wave:per-env mutex — 目标 env 上有任意 busy(BED install /
        // uninstall / req / start / stop / delete)→ 拒,不弹 dialog。对话框本身是模
        // 态会阻塞用户,但 Install 命令可能在另一个线程跑(单 env 选中的场景),这
        // 个 guard 防止 dialog 关闭后 BaseEnvInstaller.Upsert(BedStatus="done")
        // 复活刚被 uninstall 清空的字段。
        var busyEnv = existingEnvs.FirstOrDefault(e => e is not null && IsEnvBusy(e!));
        if (busyEnv is not null)
        {
            ShowInfoDialog(
                $"env '{busyEnv.Name}' 正在执行其他操作,请稍候",
                "无法部署基础环境");
            return;
        }

        // v0.6.6:env-list 工具栏 BED 入口也弹 picker(跟 BaseEnv tab 行为一致)。
        // Single 模式:用户选一个 torch+CUDA 组合 → 传给 BaseEnvProgressDialog。
        // v0.6.6.1 hotfix:改用 LoadAsync()(同源 user override JSON → live pytorch.org →
        // hardcoded fallback)— 之前 sync GetHardcodedDefaults() 只暴露 torch 2.4.1 +
        // nightly,picker dialog 已能选全版本,这里也跟上让 user override 也镜像过来。
        // v0.6.11+ T1 (G4):删 BaseEnvViewModel 后,MarkIncompatibleOlderVersions
        // (torch<2.4 profile "不推荐" suffix)必须继续生效。_profileLoader.LoadAsync()
        // 内部 GetHardcodedDefaults/BuildLiveDefaults 都会过 MarkIncompatibleOlderVersions,
        // 所以 G4 invariant 通过这条 LoadAsync 链路自动保留——T1 测试
        // BaseEnvCommand_InvokesProfileLoaderLoadAsync + MarkIncompatibleOlderVersions_OldTorchVersion_AppendsSuffix
        // 验这条链。
        var profiles = await _profileLoader.LoadAsync();
        var preselected = profiles.FirstOrDefault();
        var picked = PickerDialogOverride is not null
            ? PickerDialogOverride(profiles, preselected, PickerSelectionMode.Single)
            : BaseEnvProfilePickerDialog.Show(profiles, preselected, PickerSelectionMode.Single);
        if (picked is null || picked.Count == 0)
        {
            // 用户取消或没选 → 给个轻提示
            ShowAlreadyInstalled("请选择一个基础环境版本后再部署");
            return;
        }
        var profile = picked.First();

        // 锁住所有目标 env,dialog 关闭(成功/失败/取消)后释放。
        foreach (var e in existingEnvs) MarkEnvBusy(e!, BusyKind.BEDInstall);
        try
        {
            if (ShowProgressDialogOverride is not null)
            {
                ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
                return;
            }
            Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
        }
        finally
        {
            foreach (var e in existingEnvs) UnmarkEnvBusy(e!);
            // BED dialog 关窗后 reload:Installer 末尾已写 env.BedStatus,
            // UI 立即重读反映新状态(否则用户看到行还是旧的 "未装")
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// v0.6.11+ T1:env-list 行 toggle 按钮触发的单 env BED install 流程 — picker → progress
    /// dialog → 装完写 IsBaseEnvInstalled / BaseEnvButtonText 让 toggle 切到"卸载基础环境"。
    /// 工具栏 BaseEnvCommand 仍走 <see cref="OpenBaseEnvProgressAsync()"/> 工具栏版(用 Selected),
    /// 行为不变 — toggle 完走完后 Load() 末尾重算 button text。
    /// </summary>
    private async System.Threading.Tasks.Task OpenBaseEnvProgressForSingleEnvAsync(Environment env)
    {
        if (BaseEnvUninstaller.IsInstalled(env))
        {
            // all-done 短路(跟 OpenBaseEnvProgressAsync() 工具栏版一致)。
            ShowAlreadyInstalled(
                $"env '{env.Name}' 已安装基础环境,无需再装");
            return;
        }
        if (IsEnvBusy(env))
        {
            ShowInfoDialog(
                $"env '{env.Name}' 正在执行其他操作,请稍候",
                "无法部署基础环境");
            return;
        }
        var profiles = await _profileLoader.LoadAsync();
        var preselected = profiles.FirstOrDefault();
        var picked = PickerDialogOverride is not null
            ? PickerDialogOverride(profiles, preselected, PickerSelectionMode.Single)
            : BaseEnvProfilePickerDialog.Show(profiles, preselected, PickerSelectionMode.Single);
        if (picked is null || picked.Count == 0)
        {
            ShowAlreadyInstalled("请选择一个基础环境版本后再部署");
            return;
        }
        var profile = picked.First();
        var envIds = new List<string> { env.Id };
        MarkEnvBusy(env, BusyKind.BEDInstall);
        try
        {
            if (ShowProgressDialogOverride is not null)
            {
                ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
            }
            else
            {
                Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
            }
            // 装完(dialog 关闭)再重读 — IsInstalled() 看 BedStatus(dialog 关时已写)。
            // 失败路径(force-quit dialog / installer 抛)BedStatus != "done" → 留
            // "安装基础环境" label,跟 G10 失败不更新 label 一致;Load() 末尾也会重算。
            var nowInstalled = BaseEnvUninstaller.IsInstalled(env);
            env.IsBaseEnvInstalled = nowInstalled;
            env.BaseEnvButtonText = nowInstalled ? "卸载基础环境" : "安装基础环境";
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    private void ShowAlreadyInstalled(string message)
    {
        if (MessageBoxOverride is not null)
        {
            MessageBoxOverride(message);
            return;
        }
        MessageBox.Show(
            message, "已安装",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 装 ComfyUI requirements.txt(过滤 torch 行)。inline 状态面板(跟 env-start 同一行)显示
    /// 阶段 + 日志 + 错误,失败时面板持续可见等用户手动关。
    ///
    /// 取代之前的 RequirementsProgressDialog(dialog 模式用户看不到状态 — 改 inline 跟
    /// env-start 一致)。
    ///
    /// v0.6.5.19 hotfix:已装过(marker 文件 <c>.requirements_installed</c> 存在)不重跑 pip,
    /// 直接显示"已安装依赖(timestamp)"状态。pip install -r 是幂等的,但重复跑慢且
    /// 容易混淆"装过没装过"的判断。
    /// </summary>
    private async System.Threading.Tasks.Task InstallRequirementsAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;  // T4:per-env mutex

        // 已装过 → 显示已安装状态,不重跑 pip
        if (RequirementsInstaller.IsInstalled(env))
        {
            var timestamp = await ReadMarkerTimestampAsync(env);
            var alreadyInstalled = new RequirementsStatusViewModel(env, _requirementsInstaller);
            RequirementsStatus = alreadyInstalled;
            RaisePropertyChanged(nameof(RequirementsStatus));
            alreadyInstalled.MarkAlreadyInstalled(timestamp);
            return;
        }

        var status = new RequirementsStatusViewModel(env, _requirementsInstaller);
        RequirementsStatus = status;
        RaisePropertyChanged(nameof(RequirementsStatus));
        // v0.6.11+ T1:toggle 按钮"装依赖中..."状态(操作进行中显示)。
        env.RequirementsButtonText = "装依赖中...";
        MarkEnvBusy(env, BusyKind.ReqInstall);
        try
        {
            await status.RunAsync();
            // 成功 → 2s 后收起;失败/取消 → 不收起,等用户手动关(UI 提供 ✕ 按钮)
            if (status.IsComplete && !status.HasError)
            {
                // v0.6.11+ T1:成功 → toggle 按钮切到"卸依赖"。失败路径由 Load()
                // 末尾从 marker 文件重算,G10 失败不更新 label。
                env.IsRequirementsInstalled = true;
                env.RequirementsButtonText = "卸依赖";
                await Task.Delay(TimeSpan.FromSeconds(2));
                status.Hide();
            }
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// 读取 <c>.requirements_installed</c> marker 文件里的时间戳。失败 / 文件空内容 → 回退
    /// "未知"(读不出来也不阻塞 UI — 升级路径上 marker 可能是空文件或非标内容)。
    /// </summary>
    private static async Task<string> ReadMarkerTimestampAsync(Environment env)
    {
        var path = Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName);
        if (!File.Exists(path)) return "未知";
        try
        {
            var content = (await File.ReadAllTextAsync(path)).Trim();
            return string.IsNullOrEmpty(content) ? "未知" : content;
        }
        catch
        {
            return "未知";
        }
    }

    public RequirementsStatusViewModel? RequirementsStatus { get; private set; }

    /// <summary>
    /// DeleteEnvAsync:确认 → 调 EnvDeleterService(stop running + 删目录 + 删 SQLite 行)
    /// → 失败弹 MessageBox,成功 reload + RaiseCommandsChanged。
    /// </summary>
    private async System.Threading.Tasks.Task DeleteEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;  // T4:per-env mutex

        // Test seam 优先:设了 override 就走它(不论 true/false),不再 fall through
        // 到 MessageBox — 测试环境没有 UI dispatcher,弹框会挂死。
        bool confirmed;
        if (ConfirmDeleteOverride is not null)
        {
            confirmed = ConfirmDeleteOverride(env);
        }
        else
        {
            var result = MessageBox.Show(
                $"确认删除 env '{env.Name}' 吗?\n此操作会删除 env 目录及所有数据,不可撤销。",
                "删除环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            confirmed = result == MessageBoxResult.Yes;
        }
        if (!confirmed) return;

        MarkEnvBusy(env, BusyKind.Delete);
        try
        {
            await _envDeleter.DeleteAsync(env);
        }
        catch (EnvDeleterService.DeleteException ex)
        {
            MessageBox.Show(
                $"删除 env '{env.Name}' 失败 ({ex.Code}):\n{ex.Message}",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"删除 env '{env.Name}' 异常:\n{ex.Message}",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// UninstallBaseEnvAsync:env 行点"卸载基础环境" → confirm dialog → 调
    /// BaseEnvUninstaller.Uninstall(env)清字段(BedStatus/BedProfileId/BedFailedReason)
    /// → 持久化(VM 见的是 concrete EnvironmentRepository,所以 Upsert)→ 2s Hide。
    /// 失败弹 status.Fail + 等用户手动关。per-env mutex 防并发覆盖。
    /// </summary>
    private async System.Threading.Tasks.Task UninstallBaseEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        var status = new BaseEnvUninstallStatusViewModel();
        BaseEnvUninstallStatus = status;
        RaisePropertyChanged(nameof(BaseEnvUninstallStatus));
        status.Begin();
        // v0.6.11+ T1:toggle 按钮"卸载基础环境中..."状态(操作进行中显示)。
        env.BaseEnvButtonText = "卸载基础环境中...";
        MarkEnvBusy(env, BusyKind.BEDUninstall);
        try
        {
            // 二次确认 dialog(v0.6.5.19 MessageBoxOverride 同模式)。
            // 测试用 ShowConfirmDialogOverride 拦截,prod 默认接受(VM 留 hook 给后续
            // commit 接更完整的 confirm dialog;这里走 MessageBoxShow 兜底)。
            bool proceed;
            if (ShowConfirmDialogOverride is not null)
            {
                proceed = ShowConfirmDialogOverride(
                    $"确定要卸载基础环境吗?\nenv: {env.Name}\nvenv 文件会保留,可重新部署。",
                    "卸载基础环境");
            }
            else
            {
                var dialog = MessageBox.Show(
                    $"确定要卸载基础环境吗?\nenv: {env.Name}\nvenv 文件会保留,可重新部署。",
                    "卸载基础环境",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                proceed = dialog == MessageBoxResult.Yes;
            }
            if (!proceed)
            {
                status.Fail("用户取消");
                return;
            }

            var result = _baseEnvUninstaller.Uninstall(env);
            if (result.EnvWasRunning)
            {
                ShowInfoDialog(
                    "env 正在运行,请先停止",
                    "无法卸载");
                status.Fail("env 正在运行,请先停止");
                return;
            }
            if (result.AlreadyUninstalled)
            {
                status.Fail("env 未安装基础环境,无需卸载");
                return;
            }

            // 持久化重置后的字段(VM 见 concrete repo,Upsert 等价于 SaveAsync)
            _repo.Upsert(env);
            // v0.6.11+ T1:成功 → toggle 按钮切回"安装基础环境"。失败路径由 Load()
            // 末尾从 BedStatus 重算,G10 失败不更新 label。
            env.IsBaseEnvInstalled = false;
            env.BaseEnvButtonText = "安装基础环境";
            status.Complete();
            await Task.Delay(TimeSpan.FromSeconds(2));
            status.Hide();
        }
        catch (Exception ex)
        {
            status.Fail($"卸载失败:{ex.Message}");
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// UninstallRequirementsAsync:env 行点"卸载依赖" → confirm dialog → 调
    /// RequirementsUninstaller.UninstallAsync(env, progress, ct) 跑 pip uninstall -y -r
    /// → 成功后删 marker。
    ///
    /// v0.6.5.22 fix-wave:每次调用 new 一个 fresh RequirementsStatusViewModel(原
    /// 来 ??= reuse 会让 StatusText 显示上一次的 env 名),CTS 每次新建 + finally
    /// Dispose + null(原来传 default token,上次的 _cts 留在 VM 里让 CancelCommand
    /// 误返 true)。
    /// </summary>
    private async System.Threading.Tasks.Task UninstallRequirementsAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        // 每次 new 一个 fresh VM(RequirementsStatusViewModel 持有 _env 字段,
        // 复用会让 StatusText 显示上一次的 env 名)
        var status = new RequirementsStatusViewModel(env, _requirementsInstaller);
        RequirementsStatus = status;
        RaisePropertyChanged(nameof(RequirementsStatus));
        status.Begin();
        // v0.6.11+ T1:toggle 按钮"卸依赖中..."状态(操作进行中显示)。
        env.RequirementsButtonText = "卸依赖中...";
        MarkEnvBusy(env, BusyKind.ReqUninstall);
        // 每次新建 CTS,finally 里 Dispose + null。传给 UninstallAsync 的 token
        // 是真的可取消的(原来传 default 是死 token,Cancel 调也没用)。
        _uninstallCts = new CancellationTokenSource();
        var ct = _uninstallCts.Token;
        try
        {
            // 早退:没装过(marker 不存在)→ 直接显示 fail,不调 uninstaller。
            // 跟 v0.6.5.19 hotfix 的"已装短路"模式对偶:已装不重跑,未装不空跑。
            if (!RequirementsInstaller.IsInstalled(env))
            {
                status.Fail("env 未装依赖,无需卸载");
                return;
            }
            bool proceed;
            if (ShowConfirmDialogOverride is not null)
            {
                proceed = ShowConfirmDialogOverride(
                    $"确定要卸载依赖吗?\nenv: {env.Name}\n会跑 pip uninstall -y -r ComfyUI/requirements.txt 的非 torch 包。",
                    "卸载依赖");
            }
            else
            {
                var dialog = MessageBox.Show(
                    $"确定要卸载依赖吗?\nenv: {env.Name}\n会跑 pip uninstall -y -r ComfyUI/requirements.txt 的非 torch 包。",
                    "卸载依赖",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                proceed = dialog == MessageBoxResult.Yes;
            }
            if (!proceed)
            {
                status.Fail("用户取消");
                return;
            }

            // v0.6.5.11:Progress<T> 包装捕获 SynchronizationContext,后台线程自动
            // marshal 回 UI 线程,避免 LogLines 在后台线程改触发 WPF 不一致。
            var progress = new Progress<string>(line => status.AppendLog(line));
            var result = await _requirementsUninstaller.UninstallAsync(env, progress, ct);
            if (result.AlreadyUninstalled)
            {
                status.Fail("env 未装依赖,无需卸载");
                return;
            }
            if (!result.Success)
            {
                status.Fail(result.Reason ?? "卸载失败");
                return;
            }
            // v0.6.11+ T1:成功 → toggle 按钮切回"装依赖"。失败路径由 Load()
            // 末尾从 marker 文件重算,G10 失败不更新 label。
            env.IsRequirementsInstalled = false;
            env.RequirementsButtonText = "装依赖";
            status.Complete();
            await Task.Delay(TimeSpan.FromSeconds(2));
            status.Hide();
        }
        catch (Exception ex)
        {
            status.Fail($"卸载失败:{ex.Message}");
        }
        finally
        {
            // Dispose + null:避免下次 invocation 启动时 CancelCommand.CanExecute
            // 返 true 但 Cancel 实际不命中(指向已 disposed 的 CTS)
            _uninstallCts?.Dispose();
            _uninstallCts = null;
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    private void ShowInfoDialog(string message, string title)
    {
        if (ShowMessageBoxOverride is not null)
        {
            ShowMessageBoxOverride(message, title);
            return;
        }
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// v0.6.11+ T3:env-list 行点 toggle 按钮 → 根据 IsComfyUiManagerInstalled 切换
    /// Install / Uninstall → 重新检测(避免 stale)→ 成功 → 2s Hide;失败 → 面板持续
    /// 可见等用户关。per-env mutex 防并发(BusyKind.ComfyUiManagerInstall/Uninstall)。
    /// 重新检测后强制 Load() 让行内其他命令(Catalog 安装 ComfyUI Manager 入口等)状态同步。
    /// 暴露 internal 是为了让测试能直接 await(避免绕 RelayCommand fire-and-forget)。
    /// </summary>
    internal async System.Threading.Tasks.Task ToggleComfyUiManagerAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        var wasInstalled = env.IsComfyUiManagerInstalled;
        var status = new ComfyUIManagerStatusViewModel(env);
        ComfyUiManagerStatus = status;
        RaisePropertyChanged(nameof(ComfyUiManagerStatus));
        status.Begin();

        var busyKind = wasInstalled ? BusyKind.ComfyUiManagerUninstall : BusyKind.ComfyUiManagerInstall;
        MarkEnvBusy(env, busyKind);
        try
        {
            // Progress<string> 包装捕获 SynchronizationContext(UI 线程),后台线程
            // Report 自动 marshal 回 UI 线程 — 跟 v0.6.5.11 EnvListVM 修 LogLines
            // ObservableCollection 跨线程崩溃的模式一致。
            var progress = new Progress<string>(line => status.Report(line));
            NodeOperationResult result;
            if (wasInstalled)
            {
                result = _comfyUiManagerInstaller.Uninstall(env);
            }
            else
            {
                result = await _comfyUiManagerInstaller.InstallAsync(env, progress, CancellationToken.None);
            }

            // 重新检测(避免 stale)— 即使 result.Success,目录可能已被外部删除/装
            // 失败回滚;以文件系统为唯一真相。
            var nowInstalled = _comfyUiManagerInstaller.IsInstalled(env);
            env.IsComfyUiManagerInstalled = nowInstalled;
            env.ComfyUiManagerButtonText = nowInstalled ? "卸载 ComfyUI Manager" : "安装 ComfyUI Manager";

            if (!result.Success)
            {
                status.Fail(result.Reason ?? "未知错误");
                // 不收起,等用户手动关 — 用户能看到错误
            }
            else
            {
                status.Complete(nowInstalled ? "卸载 ComfyUI Manager 完成" : "ComfyUI Manager 安装完成");
                await Task.Delay(TimeSpan.FromSeconds(2));
                status.Hide();
            }
        }
        catch (Exception ex)
        {
            status.Fail($"操作失败:{ex.Message}");
        }
        finally
        {
            UnmarkEnvBusy(env);
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// 测试 seam — 让测试能验 toggle 按钮 disabled-when-busy(其他命令的 busy 状态由
    /// 真实长操作触发,Toggle 命令没有"已存在"形态可触发 busy)。生产代码不需要这个,
    /// 直接从 IsEnvBusy 走。
    /// </summary>
    internal void SetComfyUiManagerBusyForTest(Environment env)
    {
        MarkEnvBusy(env, BusyKind.ComfyUiManagerInstall);
    }

    /// <summary>
    /// v0.6.11+ T1:Requirements toggle 路由 — 已装 → uninstall,未装 → install。
    /// 复用现有 InstallRequirementsAsync / UninstallRequirementsAsync 子命令
    /// (v0.6.5.12 / v0.6.5.22 已落地),不重写 pip / uninstall 逻辑。
    /// 暴露 internal 是为了让测试能直接 await(避免绕 RelayCommand fire-and-forget)。
    /// </summary>
    internal async System.Threading.Tasks.Task ToggleRequirementsAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        if (env.IsRequirementsInstalled)
            await UninstallRequirementsAsync(env);
        else
            await InstallRequirementsAsync(env);
    }

    /// <summary>
    /// v0.6.11+ T1:BED toggle 路由 — 已装 → uninstall,未装 → 走 picker dialog install。
    /// 复用 OpenBaseEnvProgressAsync(per-env overload)— 工具栏入口 BaseEnvCommand 仍
    /// 走原版(用 Selected)行为不变。
    /// 暴露 internal 是为了让测试能直接 await。
    /// </summary>
    internal async System.Threading.Tasks.Task ToggleBaseEnvAsync(Environment? env)
    {
        if (env is null) return;
        if (IsEnvBusy(env)) return;

        if (env.IsBaseEnvInstalled)
            await UninstallBaseEnvAsync(env);
        else
            await OpenBaseEnvProgressAsync(env);
    }

    /// <summary>
    /// v0.6.15.8 T5:env-list 行 "管理节点" 按钮 — 弹/切 NodeManagement 底部面板。
    /// Per-env cache:同 env 再开 = 复用之前 VM(保留 selected row / scroll / 弹窗状态);
    /// 切 env = 走 cache miss → 构造新 VM,旧 env VM 留在 cache 不释放(再次切回还是同一个)。
    /// CloseRequested 事件由 panel 的 ✕ 按钮触发,把当前显示清空,VM 仍在 cache 里备 re-open。
    /// </summary>
    private void OpenNodeManagement(Environment? env)
    {
        if (env is null) return;
        if (_nodeRepo is null) return;  // 测试 ctor 没注入 → 无 nodes 可管
        if (!_nodeMgmtCache.TryGetValue(env.Id, out var vm))
        {
            // T2 R1:ctor 加 envRepo + catalogRepo + versionRepo 三个参数(从 T5
            // 前移到 T2),让生产路径能传真值给 CatalogEntryPickerDialog.Show,
            // 不再传 null! 占位。
            vm = new NodeManagementViewModel(
                _nodeRepo, _nodeOps, _errorBanner,
                _repo, _catalogRepo!, _versionRepo!,
                _requirementsInstaller,
                env.Id, env.Name);
            // CloseRequested 是 VM 触发的"自身关闭"信号,在这里把当前显示清空
            // (VM 留在 cache,re-open 时复用)。注意:不能 `NodeManagement = null`
            // 触发 vm 解绑后 Reentry — 直接赋值即可。
            vm.CloseRequested += () => NodeManagement = null;
            _nodeMgmtCache[env.Id] = vm;
        }
        NodeManagement = vm;
    }

    /// <summary>
    /// OpenInstallNodePicker:从 env 行点"安装节点" → 弹 CatalogEntryPickerDialog,
    /// picker 自己管安装(行内 InstallCommand,不再弹 InstallDialog)。
    ///
    /// v0.6.14 T5:简化 — picker 接管 install,只需 onInstallSuccess 回调(等同 v0.6.11
    /// InstallDialog 的同名参数 — 装成功时 fire-and-forget 触发 MainViewModel.RestartEnvAsync)
    /// + onClosed(picker 关时刷新 env-list)。
    /// </summary>
    private void OpenInstallNodePicker(Environment? env)
    {
        if (env is null) return;

        if (OpenInstallPickerOverride is not null)
        {
            OpenInstallPickerOverride(env);
            return;
        }

        // v0.6.11+ SDD D1:_mvm null = EnvListVM 早于 MVM 构造(测试或极端 wiring)
        // → 不传 onInstallSuccess,装成功不触发重启,行为跟 v0.6.11 既有兼容。
        Func<string, Task>? onInstallSuccess = _mvm is not null ? _mvm.RestartEnvAsync : null;

        // v0.6.14 T5:picker 自己管 install,不再需要 CatalogPickerOverride 返 entry
        // 也不再调 InstallDialog.Show。CatalogPickerOverride / InstallDialogShowOverride
        // 仍保留为 no-op 测试 seam(向后兼容现有 test suite),生产路径直接 Show。
        if (_catalogRepo is null || _nodeRepo is null || _versionRepo is null)
        {
            // v0.6.14 R1 fix:EnvListVM 在测试 / 旧 wiring 下 catalogRepo / nodeRepo 未注入,
            // 走不了 Show。直接 return(不再 fallback InstallDialog — T5 删了)。
            // 正常生产路径这三个 repo 必非空(App.xaml.cs 注入)。
            return;
        }

        Views.CatalogEntryPickerDialog.Show(
            _repo, _nodeOps, _catalogRepo, _nodeRepo, _versionRepo, _requirementsInstaller, _logger, env.Id,
            onInstallSuccess: onInstallSuccess,
            onClosed: () => Load());
    }

    /// <summary>
    /// Test seam — unit tests set this to intercept the picker launch
    /// (Show calls Application.Current.MainWindow and would throw in test context).
    /// Receives the env the command was bound to.
    /// </summary>
    public Action<Environment>? OpenInstallPickerOverride { get; set; }

    /// <summary>
    /// v0.6.11+ SDD D1 R1 (test seam):保留为 no-op,production code 在 v0.6.14 T5
    /// 已不再调 InstallDialog.Show(由 picker 行内完成)。测试 suite 仍可赋值,生产
    /// 代码忽略 — 保持旧测试用例不抛 NRE。
    /// </summary>
    [Obsolete("v0.6.14 T5:picker 行内安装,不再调 InstallDialog.Show。此 seam 仅作向后兼容保留。")]
    public Func<CatalogEntry?>? CatalogPickerOverride { get; set; }

    /// <summary>
    /// v0.6.11+ SDD D1 R1 (test seam):保留为 no-op,production code 在 v0.6.14 T5
    /// 已不再调 InstallDialog.Show(由 picker 行内完成)。测试 suite 仍可赋值,
    /// production 永远不 invoke — 保持旧测试用例不抛 NRE。
    /// </summary>
    [Obsolete("v0.6.14 T5:picker 行内安装,不再调 InstallDialog.Show。此 seam 仅作向后兼容保留。")]
    public Action<string, Func<string, Task>?>? InstallDialogShowOverride { get; set; }

    /// <summary>
    /// v0.6.7:生成 env 组件报告 HTML,写到 &lt;projectRoot&gt;/reports/,用默认浏览器打开。
    /// 报告是只读采集(pip show / pip list / git rev-parse),不改 env,所以不占 per-env
    /// 互斥锁 —— 但 env 正忙时禁用按钮,避免读到装到一半的状态。
    /// </summary>
    private async Task ReportComponentsAsync(Environment? env)
    {
        if (env is null) return;
        try
        {
            var builder = ComponentReportBuilderOverride ?? new EnvComponentReportBuilder(
                _profileLoader, _repo, ResolveGitExeForReport(), ResolveAppVersion());
            var report = await builder.BuildAsync(env);
            var html = EnvComponentReportRenderer.Render(report);

            var dir = Path.Combine(_projectRoot, "reports");
            Directory.CreateDirectory(dir);
            var fileName = $"env-{SanitizeFileName(env.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.html";
            var path = Path.Combine(dir, fileName);

            // 用 UTF-8 BOM 写,否则浏览器可能把中文当 GBK 显示乱码。
            var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            await File.WriteAllTextAsync(path, html, utf8Bom);

            (OpenReportFileOverride ?? DefaultOpenReportFile)(path);
        }
        catch (Exception ex)
        {
            ShowInfoDialog($"生成组件报告失败:{ex.Message}", "组件报告");
        }
    }

    /// <summary>
    /// v0.6.7 T2:最近一次 ReportComponentsAsync 的 Task(给测试 await,避免
    /// async void 风格的 fire-and-forget race)。生产代码无 consumer。
    /// </summary>
    internal Task? LastReportTask { get; private set; }

    /// <summary>
    /// v0.6.7 T2:ReportComponentsCommand 的 execute wrapper — 把 Task 存到
    /// <see cref="LastReportTask"/> 让测试可以 await,生产环境仍然是 fire-and-forget。
    /// </summary>
    private async void ReportComponentsExecuteWrapper(Environment? env)
    {
        var task = ReportComponentsAsync(env);
        LastReportTask = task;
        try { await task; }
        catch { /* 已在 ReportComponentsAsync 内部 ShowInfoDialog */ }
    }

    private void DefaultOpenReportFile(string path)
    {
        // v0.6.10 T2:改走 BrowserLauncher(Chrome 优先 → 默认浏览器 → ErrorBanner Warn)。
        // OpenReportFileOverride 已设的话不会到这里。
        (BrowserLauncherOverride ?? _browserLauncher)?.OpenWithChromeFallback(path, ReportOpenError);
    }

    /// <summary>
    /// v0.6.10 T2:BrowserLauncher 失败回调 → 主窗口 ErrorBanner(非 MessageBox,跟组件报告按钮
    /// 行为跟 OpenBrowser 完全一致)。
    /// </summary>
    private void ReportOpenError(string code, string message, ErrorSeverity severity)
    {
        _errorBanner?.Add(code, message, severity);
    }

    private string ResolveGitExeForReport()
        => string.IsNullOrWhiteSpace(_settings?.GitExe) ? "git" : _settings.GitExe;

    private static string ResolveAppVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "env";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// v0.6.7.2:打开运行中 ComfyUI 的页面(用户原话"用 Chrome 浏览器开启")。
    /// 只在 env.Status == "running" 且 env.Port 有值时按钮才 enabled(CanExecute gate),
    /// 所以走到这里就有 url。
    /// v0.6.10 T2:Chrome 优先 / 默认浏览器 fallback / ErrorBanner Warn 行为抽到
    /// <see cref="BrowserLauncher"/>,组件报告 + OpenBrowser 共享同一 impl。
    /// </summary>
    private void OpenBrowser(Environment? env)
    {
        if (env?.Port is not int port) return;
        var url = $"http://127.0.0.1:{port}";
        if (OpenBrowserUrlOverride is not null)
        {
            OpenBrowserUrlOverride(url);
            return;
        }
        (BrowserLauncherOverride ?? _browserLauncher)?.OpenWithChromeFallback(url, ReportOpenError);
    }

    /// <summary>
    /// v0.6.22 T4:env-list Row 0 col 2 新增"进入虚拟环境"图标按钮 handler — 启动
    /// cmd.exe /k cd /d 到 env.VenvPath,在新窗口打开该环境的虚拟环境。
    /// UseShellExecute=true 是关键(/k 需要一个真正的 console host,不带的话窗口
    /// 进程会立刻退出)。失败仅 _logger.Warn,不弹窗(env-list inline UI 不阻塞)。
    /// </summary>
    private void OpenVenv(Environment? env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.VenvPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"cd /d \\\"{env.VenvPath}\\\"\"",
                UseShellExecute = true,
            });
            _logger?.Info("env-venv-open", $"env='{env.Name}' venv='{env.VenvPath}'");
        }
        catch (Exception ex)
        {
            _logger?.Warn("env-venv-open", $"failed to open venv for env='{env.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// v0.6.22 T5:env-list 行"模板更新"按钮 handler — destructive 操作,wipe
    /// env.ComfyuiSource 内容 + git clone comfyanonymous/ComfyUI --depth=1。
    /// 流程:MessageBox 二次确认 → per-env mutex(TemplateUpdate) → 跑
    /// <see cref="ComfyUITemplateUpdater.UpdateAsync"/> → 状态写回
    /// <see cref="TemplateUpdateStatus"/>。
    /// </summary>
    private async Task UpdateTemplateAsync(Environment? env)
    {
        if (env is null || _templateUpdater is null) return;
        if (!ConfirmDangerous(
            $"模板更新会删除 env '{env.Name}' 的 ComfyUI 目录全部内容并重新 git clone。\n" +
            "未提交的修改会丢失。是否继续?"))
            return;
        var kind = BusyKind.TemplateUpdate;
        if (!_envBusy.TryAdd(env.RootPath, kind)) return;   // already busy
        try
        {
            await TemplateUpdateStatus.RunAsync(async progress =>
            {
                var result = await _templateUpdater.UpdateAsync(env, progress);
                if (!result.Success)
                {
                    TemplateUpdateStatus.Error = result.Reason ?? "未知错误";
                }
            });
        }
        finally
        {
            _envBusy.Remove(env.RootPath);
            // wipe 后 ComfyuiSource 内容变了 → reload envs 让 UI 反映最新状态
            // (env.ComfyuiSource 字段未变,但 disk state 变了)。
            Load();
            RaiseCommandsChanged();
        }
    }

    /// <summary>
    /// v0.6.22 T5:destructive 操作前的 MessageBox 二次确认 — 模板更新 / 删除等
    /// 可能丢数据 / 大改 disk state 的操作使用。走 MessageBox.YesNo 警告,
    /// 用户选 No → 返回 false(调用方直接 return 不再执行后续)。
    /// </summary>
    private bool ConfirmDangerous(string message)
    {
        var result = MessageBox.Show(
            message,
            "确认危险操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    // v0.6.10 T2:DefaultOpenBrowser + ResolveChromePath 移到 BrowserLauncher。
    // OpenBrowserUrlOverride 走 path(string) 拦截的测试 seam 仍由既有测试使用
    // (EnvironmentListViewModelOpenBrowserTests.cs 4 处)。

    private void RaiseCommandsChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ShowLogCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        InstallNodeCommand.RaiseCanExecuteChanged();
        InstallRequirementsCommand.RaiseCanExecuteChanged();
        UninstallBaseEnvCommand.RaiseCanExecuteChanged();
        UninstallRequirementsCommand.RaiseCanExecuteChanged();
        ReportComponentsCommand.RaiseCanExecuteChanged();
        OpenBrowserCommand.RaiseCanExecuteChanged();
        ToggleComfyUiManagerCommand.RaiseCanExecuteChanged();
        // v0.6.11+ T1:toggle 命令也要 refresh,否则 busy 切换后按钮不会自动 enable/disable
        ToggleRequirementsCommand.RaiseCanExecuteChanged();
        ToggleBaseEnvCommand.RaiseCanExecuteChanged();
        // v0.6.15.8 T5:NodeManagement open 命令的 CanExecute 依赖 IsEnvBusy(env),
        // busy 状态变化要 refresh 让按钮 enable/disable。
        OpenNodeManagementCommand.RaiseCanExecuteChanged();
        // v0.6.17:启动 / 关面板 / 启动成功 / 删除 env 都会改 _startStatuses dict,
        // "再次打开" 按钮要 refresh。
        ReopenStartStatusCommand.RaiseCanExecuteChanged();
        // v0.6.22 T5:模板更新命令也依赖 IsEnvBusy + env.ComfyuiSource 存在,
        // wipe + clone 后要 refresh 让按钮重新 enable。
        UpdateTemplateCommand.RaiseCanExecuteChanged();
    }
}
