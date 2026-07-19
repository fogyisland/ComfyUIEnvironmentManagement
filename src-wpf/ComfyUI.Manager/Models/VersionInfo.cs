namespace ComfyUI.Manager.Models;

/// <summary>
/// VersionInfo:单条 release 信息,用于详情面板的版本下拉/历史列表。
/// </summary>
public class VersionInfo
{
    public string Tag { get; set; } = "";
    public string PublishedAt { get; set; } = "";  // ISO 8601,如 "2026-07-15T10:30:00Z"
    public bool IsPrerelease { get; set; }

    /// <summary>
    /// "v1.2.3 · 2026-07-15" 或 prerelease "v1.2.3-rc1 · 2026-07-15 · 预发布"
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var date = PublishedAt.Length >= 10 ? PublishedAt[..10] : PublishedAt;
            var label = $"{Tag} · {date}";
            if (IsPrerelease) label += " · 预发布";
            return label;
        }
    }
}
