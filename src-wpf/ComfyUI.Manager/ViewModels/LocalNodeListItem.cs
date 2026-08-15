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

    /// <summary>
    /// v0.6.15.1 hotfix:repo URL 显示 — 从 git URL 抽出 <c>host/owner/repo</c> 形式短串
    /// (github / gitlab / bitbucket)。支持 https / http / ssh / git@ 四种形式,
    /// 其它形态保留原 URL。空串表示"没 URL"。
    /// </summary>
    public string RepositoryUrlDisplay
    {
        get
        {
            var url = Info.RepositoryUrl;
            if (string.IsNullOrWhiteSpace(url)) return "";
            var trimmed = url.Trim();
            // 1. 去 .git 后缀
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^4];
            }
            // 2. 按 scheme 去前缀,把 host/owner/repo 段拿出来
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["https://".Length..];
            }
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["http://".Length..];
            }
            else if (trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["ssh://".Length..];
                // ssh:// 可能还带 git@ 前缀
                if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed["git@".Length..];
                }
            }
            else if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                // git@host:owner/repo → host/owner/repo
                trimmed = trimmed["git@".Length..];
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx >= 0)
                {
                    trimmed = trimmed[..colonIdx] + "/" + trimmed[(colonIdx + 1)..];
                }
            }
            // 现在 trimmed 形如 "host/owner/repo" 或 "host/owner/repo/..."
            return trimmed;
        }
    }

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