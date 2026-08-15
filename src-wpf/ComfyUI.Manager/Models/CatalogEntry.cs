using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// CatalogEntry:row of the <c>catalog_cache</c> table.
/// </summary>
public class CatalogEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = "";
    [JsonPropertyName("package")]
    public string Package { get; set; } = "";
    [JsonPropertyName("raw_metadata")]
    public Dictionary<string, object?> RawMetadata { get; set; } = new();
    [JsonPropertyName("cached_at")]
    public string CachedAt { get; set; } = "";
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }

    // v0.6.7.4: 从 raw_metadata 抽出的 typed 字段(G6:raw_metadata 仍完整保留作 fallback)
    [JsonIgnore] public string? Author { get; init; }
    [JsonIgnore] public string? Description { get; init; }
    [JsonIgnore] public string? InstallType { get; init; }
    [JsonIgnore] public string? Reference { get; init; }
    [JsonIgnore] public string? LastUpdate { get; init; }

    // 解析后的 pip requirements 列表(从 pip_json 反序列化)
    [JsonIgnore] public IReadOnlyList<PipRequirement> PipRequirements { get; init; }
        = Array.Empty<PipRequirement>();

    // v0.6.13-B: GitHub metadata 抓取后填回的 11 个字段(由 GitHubCatalogMetadataService 写入)
    [JsonPropertyName("license")]
    public string? License { get; set; }
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    [JsonPropertyName("stars")]
    public int Stars { get; set; }
    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }
    [JsonPropertyName("last_commit")]
    public string? LastCommit { get; set; }   // ISO 8601 UTC
    [JsonPropertyName("readme_markdown")]
    public string? ReadmeMarkdown { get; set; }
    [JsonPropertyName("latest_changelog")]
    public string? LatestChangelog { get; set; }
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; set; }
    [JsonPropertyName("python_compat")]
    public IReadOnlyList<string> PythonCompat { get; set; } = Array.Empty<string>();
    [JsonPropertyName("os_compat")]
    public IReadOnlyList<string> OsCompat { get; set; } = Array.Empty<string>();
    [JsonPropertyName("metadata_fetched_at")]
    public string? MetadataFetchedAt { get; set; }  // ISO 8601 UTC

    // v0.6.14: 8 个新 GitHub 字段(由 GitHubCatalogMetadataService 从 /repos + /releases 提取)
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }
    [JsonPropertyName("open_issues_count")]
    public int OpenIssuesCount { get; set; }
    [JsonPropertyName("release_tag")]
    public string? ReleaseTag { get; set; }
    [JsonPropertyName("subscribers_count")]
    public int SubscribersCount { get; set; }
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }  // ISO 8601 UTC from /repos.created_at

    // v0.6.15: 由 CatalogViewModel 在 Search() 后 populate,
    // 指示此 catalog entry 对应 package 是否已下载到本地节点目录
    // (scanned_nodes 中 EnvId="" + Source="download" 的 sentinel 行存在)。
    // 不持久化到 catalog_cache 表 — 纯运行时派生属性,跟当前本地下载状态一致。
    [JsonIgnore] public bool IsInLocalNodeDb { get; set; }
}