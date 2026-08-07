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
    private readonly RequirementsInstaller _requirementsInstaller;
    private readonly BaseEnvUninstaller _baseEnvUninstaller;
    private readonly RequirementsUninstaller _requirementsUninstaller;
    private readonly string _projectRoot;

    /// <summary>
    /// v0.6.5.22 T4:per-env 互斥锁 — 同 env 上同时只允许一个长操作(BED install / uninstall /
    /// requirements install / uninstall / start / stop / delete),防止并发的 BaseEnvInstaller
    /// 在末尾 upsert BedStatus="done" 复活刚被 uninstall 清空的字段。
    /// RootPath 作 key(env.Name 可能重名)。
    /// </summary>
    private enum BusyKind { None, BEDInstall, BEDUninstall, ReqInstall, ReqUninstall, Start, Stop, Delete }

    private readonly Dictionary<string, BusyKind> _envBusy = new();

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

    public ObservableCollection<Environment> Environments { get; } = new();
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
    /// BED 卸载 inline 状态面板(env-list 操作列"卸载基础环境"按钮触发后)。单 VM,
    /// 跟 RequirementsStatusViewModel 同模式 — 完成 → 2s 自动 Hide;失败 → 等用户关。
    /// </summary>
    public BaseEnvUninstallStatusViewModel? BaseEnvUninstallStatus { get; private set; }

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
        RequirementsUninstaller? requirementsUninstaller = null)
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
        Load();
    }

    private Environment? _selected;
    public Environment? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                RaisePropertyChanged(nameof(StartTooltip));
        }
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
        var status = new EnvStartStatusViewModel();
        StartStatus = status;
        RaisePropertyChanged(nameof(StartStatus));
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
            await _launcher.StartEnvAsync(env, stageProgress, logProgress, default);
            status.Complete();
            await Task.Delay(TimeSpan.FromSeconds(2));
            status.Hide();
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

    public EnvStartStatusViewModel? StartStatus { get; private set; }

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
        var logPath = _launcher.LogFilePath(env.Id);
        LogViewerDialog.Show(env.Id, logPath);
    }

    private void CreateEnv()
    {
        var created = Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, RecentBasePythonPath);
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

    private async Task OpenBaseEnvProgressAsync()
    {
        if (Selected is null && Environments.Count == 0) return;
        var envIds = Selected is not null
            ? new List<string> { Selected.Id }
            : Environments.Select(e => e.Id).ToList();
        if (envIds.Count == 0) return;

        // v0.6.5.19.1 hotfix: env-list 工具栏"基础环境部署"按钮也加 all-done 短路 —
        // v0.6.5.19 只修了 BaseEnv tab 的 StartCommand,这个入口漏修。BedStatus 全部
        // "done" → 弹"已安装",不弹 install dialog,跟 BaseEnvViewModel.Start 行为一致。
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
        // nightly,BaseEnvView tab 已能选全版本,这里也跟上让 user override 也镜像过来。
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
        MarkEnvBusy(env, BusyKind.ReqInstall);
        try
        {
            await status.RunAsync();
            // 成功 → 2s 后收起;失败/取消 → 不收起,等用户手动关(UI 提供 ✕ 按钮)
            if (status.IsComplete && !status.HasError)
            {
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
    /// OpenInstallNodePicker:从 env 行点"安装节点" → 弹 CatalogEntryPickerDialog 选条目
    /// → 弹 InstallDialog(预填 env) → 用户选则 install。
    /// </summary>
    private void OpenInstallNodePicker(Environment? env)
    {
        if (env is null) return;

        if (OpenInstallPickerOverride is not null)
        {
            OpenInstallPickerOverride(env);
            return;
        }

        var entry = Views.CatalogEntryPickerDialog.Show();
        if (entry is null) return;

        Views.InstallDialog.Show(_repo, _nodeOps, entry, preselectedEnvId: env.Id);
    }

    /// <summary>
    /// Test seam — unit tests set this to intercept the picker + InstallDialog launch
    /// (both call Application.Current.MainWindow and would throw in test context).
    /// Receives the env the command was bound to.
    /// </summary>
    public Action<Environment>? OpenInstallPickerOverride { get; set; }

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
    }
}
