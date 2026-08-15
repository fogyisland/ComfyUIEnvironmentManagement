using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class InstallDialogViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly NodeOperations _ops;
    public CatalogEntry Entry { get; }
    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand InstallCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    /// <summary>
    /// 预填 env:从 EnvironmentList 行点"安装节点" → 走 CatalogEntryPicker → InstallDialog,
    /// 想直接装到当前 env,不是让用户从所有 env 里再选一次。null = 不预填,默认选列表第一条。
    /// </summary>
    public string? PreselectedEnvId { get; }

    /// <summary>
    /// v0.6.11 T3: 预填 tag — catalog 详情面板已选中的版本号。null = 不预填,装最新(即可
    /// 拉取的 node_versions 里第一个;pip clone 后跑 git checkout &lt;tag&gt;)。
    /// 向后兼容:caller 不传时不改变行为。
    /// </summary>
    public string? PreselectedTag { get; }

    /// <summary>
    /// v0.6.11+ SDD D1: 安装成功回调(caller 注入,典型 = <c>MainViewModel.RestartEnvAsync</c>)。
    /// null = 不触发自动重启(测试 / 离线场景)。
    /// <para>
    /// 触发语义(G7/G8/G9):
    /// <list type="bullet">
    /// <item>仅 <see cref="NodeOperationResult.Success"/>==true 时触发</item>
    /// <item>失败 / 异常路径不触发</item>
    /// <item><b>不 await</b>(fire-and-forget)— dialog 立即关,stop+start 在后台跑,
    /// env-start 进度面板在 env-list tab 显示</item>
    /// <item>用 <c>Task.Run</c> 把回调挪到 thread-pool,避免 dialog UI thread
    /// await 后 dispatcher 抛异常(跟 <c>CatalogViewModel</c> 的
    /// <c>Progress&lt;string&gt;</c> 同款模式)</item>
    /// </list>
    /// </para>
    /// </summary>
    public Func<string, Task>? OnInstallSuccess { get; }

    public InstallDialogViewModel(
        EnvironmentRepository repo,
        NodeOperations ops,
        CatalogEntry entry,
        string? preselectedEnvId = null,
        string? preselectedTag = null,
        Func<string, Task>? onInstallSuccess = null)
    {
        _repo = repo;
        _ops = ops;
        Entry = entry;
        PreselectedEnvId = preselectedEnvId;
        PreselectedTag = preselectedTag;
        OnInstallSuccess = onInstallSuccess;
        // v0.6.15.5 T3: ProgressLog 绑 ReadOnlyObservableCollection 让 XAML 安全只读
        // (T4 才加 ProgressBar + ScrollViewer)。
        ProgressLog = new ReadOnlyObservableCollection<string>(_progressLog);
        // v0.6.15.5 T3: CancelCommand 仅在 Busy 时可执行 (跟 InstallCommand 同样的 gate
        // 防止 race:启动前用户不能点取消;InstallAsync 完成后自动恢复不可点)。
        CancelCommand = new RelayCommand(_ => _cts.Cancel(), _ => Busy);
        InstallCommand = new RelayCommand(
            async _ => await InstallAsync(),
            _ => SelectedEnv is not null && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        LoadEnvs();
    }

    private Environment? _selectedEnv;
    public Environment? SelectedEnv { get => _selectedEnv; set => SetField(ref _selectedEnv, value); }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                // v0.6.15.5 T3: CancelCommand 也依赖 Busy (CanExecute),Busy 翻转时通知 UI
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string? _progress;
    public string? Progress { get => _progress; set => SetField(ref _progress, value); }

    // v0.6.15.5 T3: git 进度反馈 UI 字段
    // - ProgressPercent:用 git 输出"Receiving objects:  45%"等行里的整数百分比正则解析。
    //   无百分比时保持 0;完成时设 100。
    // - ProgressLog:全量 stderr line(已经包含"Receiving objects: X%"行),
    //   暴露为 ReadOnlyObservableCollection 给 XAML (T4) 绑 ScrollViewer。
    // - CancelCommand:RelayCommand 仅在 Busy 时可点,内部调 _cts.Cancel() 触发
    //   安装流程 cancel;UI 显示"用户取消"。
    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; set => SetField(ref _progressPercent, value); }

    private readonly ObservableCollection<string> _progressLog = new();
    public ReadOnlyObservableCollection<string> ProgressLog { get; }

    private readonly CancellationTokenSource _cts = new();
    public RelayCommand CancelCommand { get; }

    private void LoadEnvs()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
        // 优先用 PreselectedEnvId(从 EnvironmentList 行点"安装节点"过来),
        // 否则默认第一条
        if (!string.IsNullOrEmpty(PreselectedEnvId))
        {
            var match = Environments.FirstOrDefault(e => e.Id == PreselectedEnvId);
            if (match is not null)
            {
                SelectedEnv = match;
                return;
            }
        }
        if (Environments.Count > 0) SelectedEnv = Environments[0];
    }

    private async System.Threading.Tasks.Task InstallAsync()
    {
        if (SelectedEnv is null) return;
        var envId = SelectedEnv.Id;
        // CatalogEntry 没专用字段;从 raw_metadata 拿("repository" / "url")。
        // ComfyUI-Manager catalog 约定:在 raw_metadata 里有 "url" 或 "repository"。
        var repoUrl = ExtractRepoUrl(Entry);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            MessageBox.Show("catalog 条目缺 repository url", "安装节点",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Busy = true;
        Progress = "Cloning...";
        // v0.6.15.5 T3: 重置进度面板(再点同一条目重新装也要从 0 开始,不残留上次)
        ProgressPercent = 0;
        _progressLog.Clear();
        // v0.6.15.5 T3: 用 Progress<string> lambda 接 GitRunner stderr line
        // (T1 流的 IProgress<string> → T2 在 InstallAsync 透传)。Marshal 到构造时捕获的
        // SynchronizationContext,自动回到 UI 线程改 Progress/ProgressLog/ProgressPercent。
        var progress = new Progress<string>(line =>
        {
            Progress = line;
            _progressLog.Add(line);
            // git 输出形如 "Receiving objects:  45%" / "Resolving deltas: 100%"
            // 抓首个整数百分比,无百分比保持 0。
            var m = Regex.Match(line, @"(\d+)%");
            if (m.Success && double.TryParse(m.Groups[1].Value, out var p))
            {
                ProgressPercent = p;
            }
        });
        // v0.6.15.5 T3: 防止用户意外 hang(例如 git 网络半死不活)— 10 分钟上限,
        // CancelCommand 也调同一个 _cts,任一触发都进 OperationCanceledException 分支。
        _cts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            // 用 nodeId = 包名作为目录名(ComfyUI-Manager 约定)。
            // v0.6.7.5: 传 catalog PipRequirements 让 NodeOperations 在 clone 前
            // 跑 pip list diff,如有 Downgrade/Conflict 弹 modal 让用户确认是否继续。
            // 既有非 catalog 节点安装入口不传 catalogPipReqs → 走原路径不跑 diff。
            // v0.6.11 T3: 传 PreselectedTag(若 caller 显式给了),让 git checkout 钉到指定版本。
            // v0.6.15.5 T3: 传 progress + _cts.Token 让 git 输出实时反馈到 ProgressLog/ProgressPercent
            // 并支持 CancelCommand 触发取消。
            var result = await _ops.InstallAsync(
                envId, Entry.Package, repoUrl,
                targetTag: PreselectedTag,
                catalogPipReqs: Entry.PipRequirements,
                progress: progress,
                ct: _cts.Token);
            if (result.Success)
            {
                Progress = $"OK, version={result.Version}";
                // ProgressPercent 仅在 regex 匹配时更新;若 git 输出全程无百分比
                // (例如非 clone 错误情况)保持 0,避免"成功=100%"的假象。
                // v0.6.11+ SDD D1: 触发自动重启回调(fire-and-forget)。
                // - 不 await(G7):dialog 立即关,真正的 stop+start 在 background 跑,
                //   env-start 进度面板在 env-list tab 显示
                // - Task.Run(G8):把回调挪到 thread-pool,避免 dialog UI thread
                //   await 后 dispatcher 抛异常(同 CatalogViewModel:267 的 Progress<string> 模式)
                // - 失败 / 异常路径(G9)不进这分支,callback 不触发
                if (OnInstallSuccess is not null)
                {
                    _ = System.Threading.Tasks.Task.Run(
                        async () => await OnInstallSuccess(envId));
                }
                CloseRequested?.Invoke();
            }
            else
            {
                Progress = $"失败:{result.Reason}";
            }
        }
        catch (OperationCanceledException)
        {
            // v0.6.15.5 T3: CancelCommand 触发或 10 分钟安全超时都进这里。
            Progress = "用户取消";
        }
        catch (Exception ex)
        {
            Progress = $"异常:{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }
}
