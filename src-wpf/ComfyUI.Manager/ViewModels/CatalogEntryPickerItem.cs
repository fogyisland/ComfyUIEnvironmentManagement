using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.14 picker redesign: 一行 catalog 条目 + 当前 env 的安装状态。
/// 不实现 INPC —— 全 read-only derived from Entry + ScannedNode,刷新时整个 rebuild。
/// </summary>
public class CatalogEntryPickerItem
{
    public CatalogEntry Entry { get; init; } = null!;
    public bool IsInstalled { get; init; }
    public string? InstalledTag { get; init; }      // ScannedNode.ScanMeta["installed_tag"] 或 null
    public string? InstalledSha { get; init; }      // ScannedNode.Version 前 8 字符(或 null)

    /// <summary>
    /// v0.6.14 T4:此 catalog entry 的版本列表(从 node_versions 表按 published_at DESC 拉)。
    /// 空表示该 entry 没有 version metadata(row 不存在 / catalog 没跑 metadata fetch)。
    /// 取的是 item-local state,改 SelectedVersion 不 fire INPC — VM rebuild 是 refresh 路径。
    /// </summary>
    public List<VersionInfo> Versions { get; init; } = new();

    /// <summary>
    /// v0.6.14 T4:用户在该行 ComboBox 选中的 tag(version metadata 缺失时为 null)。
    /// VM 在 BuildItems 阶段默认填第一项(LatestVersion 优先,fallback ListByNode 第一条)。
    /// ComboBox 选中变化时由 XAML TwoWay binding 写回,VM 后续装该 entry 时用这个 tag。
    /// </summary>
    public string? SelectedVersion { get; set; }

    /// <summary>
    /// 已装且 catalog 有 LatestVersion 且两者不同 = 已过时。
    /// 任一为空都不算 outdated(没证据)。
    /// </summary>
    public bool IsOutdated =>
        IsInstalled
        && !string.IsNullOrEmpty(InstalledTag)
        && !string.IsNullOrEmpty(Entry.LatestVersion)
        && !string.Equals(InstalledTag, Entry.LatestVersion, StringComparison.Ordinal);

    /// <summary>显示用状态标签文本。</summary>
    public string StatusBadge =>
        !IsInstalled ? "未安装"
        : IsOutdated ? "已过时"
        : "已安装";

    /// <summary>
    /// 显示用 installed version 字符串。优先 InstalledTag(语义清晰),
    /// fallback 到 InstalledSha 前 8 字符(老节点没存 tag)。
    /// 未安装时返 null。
    /// </summary>
    public string? InstalledVersionDisplay =>
        !IsInstalled ? null
        : !string.IsNullOrEmpty(InstalledTag) ? InstalledTag
        : InstalledSha is null ? null
        : InstalledSha[..Math.Min(8, InstalledSha.Length)];

    /// <summary>XAML 用:NotInstalled / Installed / Outdated,供 DataTrigger 切 brush。</summary>
    public string StatusKind =>
        !IsInstalled ? "NotInstalled"
        : IsOutdated ? "Outdated"
        : "Installed";
}