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
}