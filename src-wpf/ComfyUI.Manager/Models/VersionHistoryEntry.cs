using System.Text.Json.Serialization;
namespace ComfyUI.Manager.Models;

public class VersionHistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("version_before")] public string? VersionBefore { get; set; }
    [JsonPropertyName("version_after")] public string? VersionAfter { get; set; }
    [JsonPropertyName("pkg_version")] public string? PkgVersion { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("performed_at")] public string PerformedAt { get; set; } = "";
}
