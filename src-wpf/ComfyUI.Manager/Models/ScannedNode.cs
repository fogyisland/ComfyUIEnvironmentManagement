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
}