using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// DepRecord:row of the <c>dep_records</c> table.
/// </summary>
public class DepRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("env_id")] public string EnvId { get; set; } = "";
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("dep_name")] public string DepName { get; set; } = "";
    [JsonPropertyName("dep_version_spec")] public string? DepVersionSpec { get; set; }
    [JsonPropertyName("scanned_at")] public string ScannedAt { get; set; } = "";
}