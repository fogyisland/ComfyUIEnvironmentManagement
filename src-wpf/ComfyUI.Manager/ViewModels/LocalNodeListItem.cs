using System;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:INPC wrapper 包 LocalNodeInfo 给 XAML DataTemplate 用。
/// BadgeText 拼 "已装: env-A, env-B" 形式,InstalledEnvNames 排序稳定。
/// </summary>
public class LocalNodeListItem : ViewModelBase
{
    public LocalNodeInfo Info { get; }

    public LocalNodeListItem(LocalNodeInfo info)
    {
        Info = info;
        UpdateBadge();
    }

    private string _badgeText = "";
    public string BadgeText
    {
        get => _badgeText;
        private set => SetField(ref _badgeText, value);
    }

    public string DisplayName => string.IsNullOrEmpty(Info.NodeId) ? "(unnamed)" : Info.NodeId;
    public string HeadShaDisplay => Info.HeadSha is { Length: >= 8 } ? Info.HeadSha[..8] : (Info.HeadSha ?? "—");

    public void UpdateBadge()
    {
        if (Info.InstalledEnvNames.Count == 0)
        {
            BadgeText = "未装到任何 env";
        }
        else
        {
            BadgeText = "已装: " + string.Join(", ", Info.InstalledEnvNames);
        }
        // Info 是 immutable record,变更通过替换整个 LocalNodeListItem 实例生效
        // (LocalNodeListViewModel.InstallAsync 走 Items[idx] = new LocalNodeListItem(newInfo));
        // 这里只 fire BadgeText 自身的 INPC 已够。
    }
}