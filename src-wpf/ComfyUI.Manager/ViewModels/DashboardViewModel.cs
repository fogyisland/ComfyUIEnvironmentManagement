using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.9 T5:Dashboard 页面的 VM。
///
/// 数据源 <see cref="IDashboardService"/> 由 T4 完成(并行 4 个 task 聚合
/// envRepo.ListAll / nodeRepo.CountAllAsync / AppLogger 解析最近 5 行 +
/// GitHub latest release)。本 VM 持有 <see cref="LastSnapshot"/> 跟
/// <see cref="IsRefreshing"/> 两个 observable 状态供 XAML 4 卡片 Grid 绑定。
///
/// **关键决策**(brief §4.1 / §6.1):
/// <list type="bullet">
/// <item><b>不自动刷新</b>:不像 <see cref="SystemStatusViewModel"/> 在 ctor 触发,
///    由 <see cref="MainViewModel.ShowDashboard"/> 在用户进 tab 时调
///    <see cref="RefreshAsync"/>(G7 性能 + 启动延迟可控)。</item>
/// <item><b>并发去重</b>:用 <see cref="SemaphoreSlim"/>(1, 1) 而非
///    CancellationToken cancel 之前 task — 用户连点刷新按钮时第二次 await
///    返回跟第一次相同的结果(GitHub 60/h 限流友好 + 用户体感直观)。</item>
/// <item><b>失败保留 LastSnapshot</b>:partial failure(G8 语义)catch 后
///    保留旧值,Dashboard 继续展示上次数据不闪空面板。</item>
/// </list>
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IDashboardService _service;
    private readonly IBrowserLauncher? _browserLauncher;
    // SemaSlim(1, 1) = 并发去重:第一个 await 抢到锁,第二个 wait — task1 完成时
    // 第二个拿到锁(此时 IsRefreshing 变 false),但 LastSnapshot 已被 task1 写入,
    // task2 不会重写(同样的快照,SetField equals return false)。CanExecute 也 gate。
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private DashboardSnapshot? _lastSnapshot;
    public DashboardSnapshot? LastSnapshot
    {
        get => _lastSnapshot;
        private set => SetField(ref _lastSnapshot, value);
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetField(ref _isRefreshing, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand RefreshCommand { get; }

    // ---- v0.6.11+ T3:欢迎首页扩展字段 ----

    private IReadOnlyList<GitHubRelease> _releases = Array.Empty<GitHubRelease>();
    /// <summary>GitHub releases 全 list(网络失败 → 保留上次,永不为 null)。</summary>
    public IReadOnlyList<GitHubRelease> Releases
    {
        get => _releases;
        private set => SetField(ref _releases, value);
    }

    private IReadOnlyList<ChangelogEntry> _changelog = Array.Empty<ChangelogEntry>();
    /// <summary>CHANGELOG.md 解析结果(service 已保证非空;空则保留上次,见 RefreshAsync)。</summary>
    public IReadOnlyList<ChangelogEntry> Changelog
    {
        get => _changelog;
        private set
        {
            if (SetField(ref _changelog, value))
                RaisePropertyChanged(nameof(VisibleChangelog));
        }
    }

    private bool _isChangelogExpanded;
    /// <summary>折叠态只显示前 5 条;展开显示全部。由 ToggleChangelogExpandCommand 翻转。</summary>
    public bool IsChangelogExpanded
    {
        get => _isChangelogExpanded;
        set
        {
            if (SetField(ref _isChangelogExpanded, value))
            {
                RaisePropertyChanged(nameof(VisibleChangelog));
                RaisePropertyChanged(nameof(ChangelogToggleLabel));
            }
        }
    }

    /// <summary>
    /// XAML 「最近更新」ItemsControl 绑这个(不是 snapshot 上的同名属性)——
    /// 展开/折叠是 VM 层的 UI 状态,snapshot 是不可变的数据快照。
    /// </summary>
    public IReadOnlyList<ChangelogEntry> VisibleChangelog =>
        IsChangelogExpanded
            ? Changelog
            : Changelog.Take(DashboardSnapshot.VisibleChangelogLimit).ToList();

    public string ChangelogToggleLabel => IsChangelogExpanded ? "▲ 收起" : "▼ 展开全部";

    private string _stagingPath = DefaultStagingPath();
    /// <summary>
    /// 本地 staging 可执行文件路径。ctor 就有值(不等 RefreshAsync)——
    /// 「下载地址」区块在首次刷新完成前也要能显示 / 复制。
    /// </summary>
    public string StagingPath
    {
        get => _stagingPath;
        private set => SetField(ref _stagingPath, value);
    }

    private string _releaseUrl = DashboardSnapshot.DefaultReleaseUrl;
    public string ReleaseUrl
    {
        get => _releaseUrl;
        private set => SetField(ref _releaseUrl, value);
    }

    private int? _gitHubStars;
    public int? GitHubStars
    {
        get => _gitHubStars;
        private set => SetField(ref _gitHubStars, value);
    }

    private int? _gitHubReleaseCount;
    public int? GitHubReleaseCount
    {
        get => _gitHubReleaseCount;
        private set => SetField(ref _gitHubReleaseCount, value);
    }

    public RelayCommand CopyStagingPathCommand { get; }
    public RelayCommand OpenStagingFolderCommand { get; }
    public RelayCommand OpenReleaseUrlCommand { get; }
    public RelayCommand ToggleChangelogExpandCommand { get; }

    /// <summary>
    /// 测试 seam:替换 Clipboard / explorer 启动等 shell 副作用。
    /// null → 走真实实现。单测不碰剪贴板(需要 STA + 会污染用户剪贴板)。
    /// </summary>
    internal Action<string>? ClipboardSetTextOverride { get; set; }
    internal Action<string>? RevealInExplorerOverride { get; set; }

    public DashboardViewModel(IDashboardService service, IBrowserLauncher? browserLauncher = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _browserLauncher = browserLauncher;
        RefreshCommand = new RelayCommand(
            execute: _ => _ = RefreshAsync(),
            canExecute: _ => !IsRefreshing);
        CopyStagingPathCommand = new RelayCommand(_ => CopyStagingPath());
        OpenStagingFolderCommand = new RelayCommand(_ => OpenStagingFolder());
        OpenReleaseUrlCommand = new RelayCommand(_ => OpenReleaseUrl());
        ToggleChangelogExpandCommand = new RelayCommand(
            _ => IsChangelogExpanded = !IsChangelogExpanded);
    }

    private static string DefaultStagingPath() =>
        Path.Combine(AppContext.BaseDirectory, "ComfyUI.Manager.exe");

    /// <summary>剪贴板失败(其他进程占用 / 非 STA)不该炸 UI —— 静默吞。</summary>
    private void CopyStagingPath()
    {
        if (string.IsNullOrEmpty(StagingPath)) return;
        try
        {
            if (ClipboardSetTextOverride is not null) ClipboardSetTextOverride(StagingPath);
            else Clipboard.SetText(StagingPath);
        }
        catch
        {
            // 忽略:复制失败不阻断用户,路径本身在 UI 上可见可手抄。
        }
    }

    /// <summary>在资源管理器里选中 staging exe;exe 不存在则退回打开其所在目录。</summary>
    private void OpenStagingFolder()
    {
        if (string.IsNullOrEmpty(StagingPath)) return;
        try
        {
            if (RevealInExplorerOverride is not null)
            {
                RevealInExplorerOverride(StagingPath);
                return;
            }

            if (File.Exists(StagingPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{StagingPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var dir = Path.GetDirectoryName(StagingPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略:打开资源管理器失败不阻断 UI。
        }
    }

    private void OpenReleaseUrl() =>
        _browserLauncher?.OpenWithChromeFallback(ReleaseUrl);

    /// <summary>
    /// 拉一次快照写进 <see cref="LastSnapshot"/>。并发去重(SemaSlim) +
    /// 失败保留旧值(G8 partial failure 不闪空面板)。
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // SemaSlim(1, 1):已经在刷 → 不排队、直接返回(no-op,避免重复 GitHub 请求)。
        // WaitAsync(0) 拿不到锁说明另一次刷新正在跑,这次直接放弃。
        if (!await _refreshLock.WaitAsync(0, ct))
        {
            return;
        }
        try
        {
            IsRefreshing = true;
            var snapshot = await _service.GetSnapshotAsync(ct);
            LastSnapshot = snapshot;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消 → 重新抛出供上层感知(cancellation 不算 partial failure)
            throw;
        }
        catch
        {
            // G8 partial failure: 保留 LastSnapshot,VM 不抛
            // (Dashboard 仍然展示上次数据,LastSnapshot 不动)
        }
        finally
        {
            IsRefreshing = false;
            // v0.6.11+ T3 修:原实现只 Wait 不 Release,信号量永久耗尽 —— 每个 VM 实例
            // 只有第一次 RefreshAsync 真的跑,之后全部在上面 return。DashboardViewModel
            // 被 MainViewModel 缓存,所以「刷新」按钮点第二次起就是死的(既有测试只断言
            // 状态不变,恰好被 no-op 满足,所以一直没暴露)。
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 把 snapshot 上的新字段摊平到 VM 可绑定属性。
    ///
    /// **空值保留策略**(G8 partial failure 的延伸):Releases / Changelog 拿到空
    /// list 时保留上一次的值 —— GitHub 限流或 CHANGELOG 临时读不到时,卡片继续显示
    /// 旧内容,而不是刷成空白。StagingPath / ReleaseUrl 是确定性常量,直接覆盖。
    /// </summary>
    private void ApplySnapshot(DashboardSnapshot snapshot)
    {
        if (snapshot.Releases.Count > 0) Releases = snapshot.Releases;
        if (snapshot.Changelog.Count > 0) Changelog = snapshot.Changelog;
        if (!string.IsNullOrEmpty(snapshot.StagingPath)) StagingPath = snapshot.StagingPath;
        if (!string.IsNullOrEmpty(snapshot.ReleaseUrl)) ReleaseUrl = snapshot.ReleaseUrl;
        GitHubStars = snapshot.GitHubStars;
        GitHubReleaseCount = snapshot.GitHubReleaseCount;
    }
}