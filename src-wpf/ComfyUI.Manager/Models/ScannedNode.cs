using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// ScannedNode:row of the <c>scanned_nodes</c> table.
/// </summary>
public class ScannedNode
{
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
    [JsonIgnore]
    public bool IsOutdated { get; set; }
}