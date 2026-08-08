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
}