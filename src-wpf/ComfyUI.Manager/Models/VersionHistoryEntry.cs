using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// VersionHistoryEntry:row of the <c>version_history</c> table.
/// </summary>
public class VersionHistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("env_id")] public string EnvId { get; set; } = "";
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("version_before")] public string? VersionBefore { get; set; }
    [JsonPropertyName("version_after")] public string? VersionAfter { get; set; }
    [JsonPropertyName("pkg_version")] public string? PkgVersion { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("performed_at")] public string PerformedAt { get; set; } = "";
}