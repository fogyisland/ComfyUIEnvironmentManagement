using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// ScannedNode:row of the <c>scanned_nodes</c> table。
/// v0.6.15.10:实现 INotifyPropertyChanged — 仅 transient 属性(目前 IsOutdated +
/// RowDetailsVisible)需要通知,持久化属性走 NodeRepository.Upsert 显式 Bind() 参数不
/// 需要 round-trip 通知。JSON 序列化只看 [JsonPropertyName] 不看事件 → 加 INPC 安全。
/// </summary>
public class ScannedNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("env_id")]
    public string EnvId { get; set; } = "";
    [JsonPropertyName("package")]
    public string Package { get; set; } = "";
    [JsonPropertyName("package_path")]
    public string PackagePath { get; set; } = "";
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("author")]
    public string? Author { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("class_mappings")]
    public List<string> ClassMappings { get; set; } = new();
    [JsonPropertyName("status")]
    public string Status { get; set; } = "enabled";
    [JsonPropertyName("scan_meta")]
    public Dictionary<string, string> ScanMeta { get; set; } = new();
    [JsonPropertyName("last_scanned_at")]
    public string? LastScannedAt { get; set; }
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
    /// <summary>
    /// 来源标记: <c>"env"</c> = env 装入; <c>"download"</c> = 纯下载到本地节点目录。
    /// 历史行 backfill 为 <c>"env"</c>(老数据默认就是 env 装入的)。
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "env";

    /// <summary>
    /// v0.6.15.1 hotfix:git 仓库 URL(<c>git clone</c> 用的完整 https/ssh 地址)。
    /// 仅本地下载行 (<c>Source="download"</c>) 有意义;env 装行通常为空。
    /// 老已下载的 node 没有此字段(<c>NULL</c>),UI 走 <c>git config remote.origin.url</c> fallback。
    /// </summary>
    [JsonPropertyName("repository_url")]
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// v0.6.15.9:transient per-row 升级 可见性 flag。NodeManagementViewModel 在 ScanAsync
    /// 末尾根据 <c>ScanMeta["installed_tag"]</c> 跟 catalog LatestVersion 比对后 set。
    /// <para>
    /// 不持久化到 DB:<see cref="NodeRepository.Upsert"/> 走显式 Bind() 参数(不是全对象
    /// JSON 序列化),所以无 [JsonPropertyName] 不会被 round-trip 写回 SQLite。
    /// </para>
    /// <para>
    /// 不在构造函数初始化(默认 <c>false</c>)— 第一次读取(从 DB ListByEnv 出来的 row)
    /// 不会有该值,直到 VM 完成首次 ScanAsync 才填。
    /// </para>
    /// </summary>
    private bool _isOutdated;
    [JsonIgnore]
    public bool IsOutdated
    {
        get => _isOutdated;
        set { if (_isOutdated != value) { _isOutdated = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// v0.6.15.9.2:UI 用的版本字符串。优先级 tag > SHA 前 7 > 空。
    /// <para>
    /// git 装的 node 通常有 <c>installed_tag</c>(v0.6.15.x 起 NodeOperations 在
    /// install/upgrade/rescan 时 <c>git describe --tags --abbrev=0</c> 抓的);手动
    /// copy 的 node 没 tag,只有 <c>Version</c> SHA(7 位够定位,用户不需要 40 位)。
    /// 都没就空 — 不要乱填"(unknown)"之类误导用户。
    /// </para>
    /// <para>
    /// 计算属性 — 没 setter,UI 绑一下就完事。scan 完填 IsOutdated 顺手也会让这个
    /// 有值(因为 ScanMeta 跟 Version 都是 scan 时填的)。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string DisplayVersion
    {
        get
        {
            string? tag = null;
            if (ScanMeta is not null && ScanMeta.TryGetValue("installed_tag", out var t)
                && !string.IsNullOrEmpty(t))
            {
                tag = t;
            }
            if (!string.IsNullOrEmpty(tag)) return tag!;
            if (!string.IsNullOrEmpty(Version) && Version!.Length >= 7) return Version!.Substring(0, 7);
            return Version ?? "";
        }
    }

    /// <summary>
    /// v0.6.15.10:行 details 展开状态(transient,跟 IsOutdated 同样的 DB 隔离原则 — NodeRepository.Upsert
    /// 走显式 Bind() 不 round-trip JSON,不会持久化)。每行 chevron 列点击 toggle 此 flag,
    /// DataGrid RowDetailsVisibility 跟着绑这个属性 → row details 展开/收起。
    /// </summary>
    private bool _rowDetailsVisible;
    [JsonIgnore]
    public bool RowDetailsVisible
    {
        get => _rowDetailsVisible;
        set { if (_rowDetailsVisible != value) { _rowDetailsVisible = value; OnPropertyChanged(); } }
    }

    // ──────────────── v0.6.15.10:scan meta display helpers ────────────────
    // 全部计算属性,无 setter。ScanMeta 是 dict<string,string>(SQLite TEXT/JSON),
    // helper 内部 TryGetValue + 安全 parse,缺/空/null 都返 fallback 字符串让 UI
    // 不报 binding 错。row details 里走 key-value 网格展示。

    /// <summary>ScanMeta["branch"] — 当前分支。非 git / detached → 空。</summary>
    [JsonIgnore]
    public string Branch => ScanMeta is not null && ScanMeta.TryGetValue("branch", out var v) ? v : "";

    /// <summary>ScanMeta["last_commit_date"] — ISO 8601。非 git → 空。</summary>
    [JsonIgnore]
    public string LastCommitDate => ScanMeta is not null && ScanMeta.TryGetValue("last_commit_date", out var v) ? v : "";

    /// <summary>ScanMeta["last_commit_author"]。非 git → 空。</summary>
    [JsonIgnore]
    public string LastCommitAuthor => ScanMeta is not null && ScanMeta.TryGetValue("last_commit_author", out var v) ? v : "";

    /// <summary>ScanMeta["last_commit_short"] — HEAD commit subject 第一行。非 git → 空。</summary>
    [JsonIgnore]
    public string LastCommitShort => ScanMeta is not null && ScanMeta.TryGetValue("last_commit_short", out var v) ? v : "";

    /// <summary>ScanMeta["is_dirty"] — "true"/"false"。UI 走 BoolToVisibility 显示 dirty badge。</summary>
    [JsonIgnore]
    public bool IsDirty =>
        ScanMeta is not null && ScanMeta.TryGetValue("is_dirty", out var v)
        && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>ScanMeta["behind_count"] — 落后 upstream 几个 commit。空 = 不知道。</summary>
    [JsonIgnore]
    public string BehindCountDisplay
    {
        get
        {
            if (ScanMeta is null || !ScanMeta.TryGetValue("behind_count", out var v) || string.IsNullOrEmpty(v))
                return "";
            return v;
        }
    }

    /// <summary>ScanMeta["disk_size"] — 字节数,UI 显示 KB/MB。</summary>
    [JsonIgnore]
    public string DiskSizeDisplay
    {
        get
        {
            if (ScanMeta is null || !ScanMeta.TryGetValue("disk_size", out var v)
                || !long.TryParse(v, out var bytes) || bytes <= 0) return "";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    /// <summary>ScanMeta["file_count"] — 文件总数。</summary>
    [JsonIgnore]
    public string FileCountDisplay =>
        ScanMeta is not null && ScanMeta.TryGetValue("file_count", out var v) && !string.IsNullOrEmpty(v)
            ? v : "";

    /// <summary>ScanMeta["python_files"] — .py 文件数。</summary>
    [JsonIgnore]
    public string PythonFileCountDisplay =>
        ScanMeta is not null && ScanMeta.TryGetValue("python_files", out var v) && !string.IsNullOrEmpty(v)
            ? v : "";

    /// <summary>ScanMeta["has_requirements"] — 存在 requirements.txt。</summary>
    [JsonIgnore]
    public bool HasRequirements =>
        ScanMeta is not null && ScanMeta.TryGetValue("has_requirements", out var v) && v == "1";

    /// <summary>ScanMeta["has_pyproject"] — 存在 pyproject.toml。</summary>
    [JsonIgnore]
    public bool HasPyproject =>
        ScanMeta is not null && ScanMeta.TryGetValue("has_pyproject", out var v) && v == "1";

    /// <summary>ScanMeta["has_init"] — 存在 __init__.py(能 load 成 ComfyUI node)。</summary>
    [JsonIgnore]
    public bool HasInit =>
        ScanMeta is not null && ScanMeta.TryGetValue("has_init", out var v) && v == "1";
}