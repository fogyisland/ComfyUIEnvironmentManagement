using System;
using System.Threading;
using System.Threading.Tasks;
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

    public DashboardViewModel(IDashboardService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        RefreshCommand = new RelayCommand(
            execute: _ => _ = RefreshAsync(),
            canExecute: _ => !IsRefreshing);
    }

    /// <summary>
    /// 拉一次快照写进 <see cref="LastSnapshot"/>。并发去重(SemaSlim) +
    /// 失败保留旧值(G8 partial failure 不闪空面板)。
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // SemaSlim(1, 1):已经在刷 → 不排队、直接返回(no-op,避免重复 GitHub 请求)。
        // 不传 timeout 给 WaitAsync — 第二次调用应该拿到锁后走完,但 LastSnapshot
        // 跟 task1 一致,SetField 静默 no-op。
        if (!await _refreshLock.WaitAsync(0, ct))
        {
            return;
        }
        try
        {
            IsRefreshing = true;
            var snapshot = await _service.GetSnapshotAsync(ct);
            LastSnapshot = snapshot;
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
        }
    }
}