using System;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15: 可复用 RateLimit banner VM。CatalogRefreshService 撞 limit 时构造
/// RateLimitInfo → IProgress.Report 触发 Show() → UI 看到 IsVisible=true +
/// Title + Message。DismissCommand 用户点 ✕ 手动隐藏；下次 refresh 入口
/// CatalogViewModel.RefreshAsync 自动调 Hide() 清 stale 状态。
/// </summary>
public class RateLimitBannerViewModel : ViewModelBase
{
    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    private string _message = "";
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public RelayCommand DismissCommand { get; }

    public RateLimitBannerViewModel()
    {
        DismissCommand = new RelayCommand(_ => Hide());
    }

    public void Show(RateLimitInfo info, DateTimeOffset now)
    {
        var waitMin = info.ResetUnix is not null
            ? Math.Max(0, (int)Math.Ceiling(
                (DateTimeOffset.FromUnixTimeSeconds(info.ResetUnix.Value) - now).TotalMinutes))
            : 0;
        var stageLabel = info.Stage switch
        {
            RateLimitStage.Version => "节点版本",
            RateLimitStage.Metadata => "catalog metadata",
            _ => "",
        };
        var resetAt = info.ResetUnix is not null
            ? DateTimeOffset.FromUnixTimeSeconds(info.ResetUnix.Value).ToLocalTime()
            : (DateTimeOffset?)null;
        Title = $"GitHub API 限流 — {stageLabel} 拉取暂停";
        Message = resetAt is null
            ? $"本次只拉取了 {info.PartialCount}/{info.TotalCount},剩余 {info.Remaining} 次配额用尽"
            : $"本次只拉取了 {info.PartialCount}/{info.TotalCount},GitHub 限流将在 {resetAt:HH:mm} 重置(约 {waitMin} 分钟后),下次 refresh 自动跳过该阶段";
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}