using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.14 picker redesign: 一行 catalog 条目 + 当前 env 的安装状态。
/// 不实现 INPC —— 全 read-only derived from Entry + ScannedNode,刷新时整个 rebuild。
/// </summary>
public class CatalogEntryPickerItem : INotifyPropertyChanged
{
    public CatalogEntry Entry { get; init; } = null!;
    public bool IsInstalled { get; init; }
    public string? InstalledTag { get; init; }      // ScannedNode.ScanMeta["installed_tag"] 或 null
    public string? InstalledSha { get; init; }      // ScannedNode.Version 前 8 字符(或 null)

    // ---- v0.6.14 T5:行内安装进度 + 错误(3 个 INPC prop,装的时候改) ----

    private bool _isInstalling;
    /// <summary>该行正在被装,UI 显示进度文本 + 禁用安装按钮。</summary>
    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetField(ref _isInstalling, value);
    }

    private string? _installProgress;
    /// <summary>NodeOperations.InstallAsync 通过 Progress&lt;string&gt; 报的阶段消息。</summary>
    public string? InstallProgress
    {
        get => _installProgress;
        set => SetField(ref _installProgress, value);
    }

    private string? _installError;
    /// <summary>装失败原因(成功时被清空)。null 时 UI 隐藏错误区。</summary>
    public string? InstallError
    {
        get => _installError;
        set => SetField(ref _installError, value);
    }

    // ---- v0.6.15.11:行 details 展开 flag(跟 v0.6.15.10 节点管理面板 chevron 模式一致) ----

    private bool _isExpanded;
    /// <summary>True = chevron toggle 开,显示 row details(3 分区:GitHub 项目元数据 / 安装兼容性 / 已装 vs 最新比对)。
    /// <para>不持久化 — 跟 <see cref="ScannedNode.RowDetailsVisible"/> 同样的设计:picker 每次重新打开就
    /// 重置成 false,避免每次打开都看到一大堆展开状态。</para>
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

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

    // ──────────────── v0.6.15.11:stat strip + row details 展开字段 ────────────────
    // 这些都是计算属性,UI 直接绑。空值返 null,UI 走 BoolToVisibility / NullToVisibility
    // 隐藏对应 chip,自然 strip 就只剩有数据的 chip。

    /// <summary>"★ 1.2K" 格式化的 stars,Stars=0 时 null。</summary>
    public string? StarsDisplay => Entry.Stars > 0 ? $"★ {FormatCount(Entry.Stars)}" : null;

    /// <summary>"↓ 56.7K" 格式化的 downloads,Downloads=0 时 null。</summary>
    public string? DownloadsDisplay => Entry.Downloads > 0 ? $"↓ {FormatCount(Entry.Downloads)}" : null;

    /// <summary>License 显示文本(原样)。无 license → null。</summary>
    public string? LicenseDisplay =>
        string.IsNullOrWhiteSpace(Entry.License) ? null : Entry.License!.Trim();

    /// <summary>Language 显示文本(原样)。无 → null。</summary>
    public string? LanguageDisplay =>
        string.IsNullOrWhiteSpace(Entry.Language) ? null : Entry.Language!.Trim();

    /// <summary>"py 3.10+3.11" 或 "py 3.10"。空 list → null。</summary>
    public string? PythonCompatDisplay
    {
        get
        {
            if (Entry.PythonCompat is null || Entry.PythonCompat.Count == 0) return null;
            var parts = new List<string>();
            foreach (var v in Entry.PythonCompat)
                if (!string.IsNullOrWhiteSpace(v)) parts.Add(v.Trim());
            if (parts.Count == 0) return null;
            // 简单拼接:取主版本号 list,展示前 3 个 + "+(剩余 N)" 计数后缀
            if (parts.Count <= 3) return "py " + string.Join("+", parts);
            return "py " + string.Join("+", parts.Take(3)) + "+" + (parts.Count - 3);
        }
    }

    /// <summary>"🪟 🍎 🐧" — windows/macos/linux → emoji。无 → null。</summary>
    public string? OsCompatIcons
    {
        get
        {
            if (Entry.OsCompat is null || Entry.OsCompat.Count == 0) return null;
            var icons = new List<string>();
            foreach (var os in Entry.OsCompat)
            {
                if (string.IsNullOrWhiteSpace(os)) continue;
                var l = os.Trim().ToLowerInvariant();
                if (l.Contains("windows") || l == "win") icons.Add("🪟");
                else if (l.Contains("macos") || l.Contains("mac") || l == "osx" || l == "darwin") icons.Add("🍎");
                else if (l.Contains("linux") || l == "ubuntu") icons.Add("🐧");
            }
            return icons.Count > 0 ? string.Join(" ", icons) : null;
        }
    }

    /// <summary>Tags 拼接成一行(逗号分隔),最多 5 个,空 list → null。</summary>
    public string? TagsDisplay
    {
        get
        {
            if (Entry.Tags is null || Entry.Tags.Count == 0) return null;
            var parts = new List<string>();
            foreach (var t in Entry.Tags)
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
            if (parts.Count == 0) return null;
            if (parts.Count <= 5) return string.Join(", ", parts);
            return string.Join(", ", parts.Take(5)) + " +" + (parts.Count - 5);
        }
    }

    /// <summary>GitHub HTML URL(只显示 host+path 前 30 字符)。无 → null。</summary>
    public string? HtmlUrlDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.HtmlUrl)) return null;
            var url = Entry.HtmlUrl!.Trim();
            // 截前 50 字符(避免过长撑爆)
            return url.Length <= 50 ? url : url[..50] + "…";
        }
    }

    /// <summary>Homepage URL(同样截前 50)。无 → null。</summary>
    public string? HomepageDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.Homepage)) return null;
            var url = Entry.Homepage!.Trim();
            return url.Length <= 50 ? url : url[..50] + "…";
        }
    }

    /// <summary>"1.2K" / "567" / "1.5M" 数字格式化。</summary>
    private static string FormatCount(int n)
    {
        if (n < 1000) return n.ToString();
        if (n < 10_000) return (n / 1000.0).ToString("F1") + "K";
        if (n < 1_000_000) return (n / 1000).ToString() + "K";
        if (n < 10_000_000) return (n / 1_000_000.0).ToString("F1") + "M";
        if (n < 1_000_000_000) return (n / 1_000_000).ToString() + "M";
        return (n / 1_000_000_000.0).ToString("F1") + "B";
    }

    /// <summary>ISO 8601 date 截前 10 字符(YYYY-MM-DD)。无 → null。</summary>
    public static string? ShortDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        var s = iso!.Trim();
        return s.Length >= 10 ? s[..10] : s;
    }

    /// <summary>PipRequirements 渲染用 list,空 → null。</summary>
    public IReadOnlyList<PipRequirement>? PipRequirementsDisplay
        => Entry.PipRequirements is { Count: > 0 } ? Entry.PipRequirements : null;

    /// <summary>
    /// v0.6.15.11:stat strip 的 chip 列表。Lazy 构建 — 因为 <see cref="Entry"/> 是 init-only
    /// property,在 ctor 里还不可用,得第一次访问时才 Build(那时 Entry 已经被 object initializer 设上)。
    /// <para>
    /// UI 走 ItemsControl + horizontal StackPanel,每个 chip 是 { Display, Tooltip } 的 Border。
    /// 空值字段不加入列表 — strip 高度自动收缩。
    /// </para>
    /// </summary>
    public IReadOnlyList<StatChip> StatChips => _statChips ??= BuildStatChips();
    private IReadOnlyList<StatChip>? _statChips;

    private List<StatChip> BuildStatChips()
    {
        var chips = new List<StatChip>();
        if (LicenseDisplay is not null) chips.Add(new StatChip(LicenseDisplay, "License"));
        if (LanguageDisplay is not null) chips.Add(new StatChip(LanguageDisplay, "Language"));
        if (StarsDisplay is not null) chips.Add(new StatChip(StarsDisplay, "Stars"));
        if (DownloadsDisplay is not null) chips.Add(new StatChip(DownloadsDisplay, "Downloads"));
        if (PythonCompatDisplay is not null) chips.Add(new StatChip(PythonCompatDisplay, "Python compat"));
        if (OsCompatIcons is not null) chips.Add(new StatChip(OsCompatIcons, "OS compat"));
        if (Entry.Deprecated) chips.Add(new StatChip("DEPRECATED", "作者已标记为不推荐"));
        return chips;
    }

    /// <summary>
    /// v0.6.15.11 stat strip 单 chip 数据。Display = 主文本,Tooltip = 长说明。
    /// </summary>
    public sealed record StatChip(string Display, string Tooltip);
}