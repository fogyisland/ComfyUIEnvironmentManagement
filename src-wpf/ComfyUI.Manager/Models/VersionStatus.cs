using System.Text.Json.Serialization;
namespace ComfyUI.Manager.Models;

public class VersionStatus
{
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("current_sha")] public string CurrentSha { get; set; } = "";
    [JsonPropertyName("current_sha_short")] public string CurrentShaShort { get; set; } = "";
    [JsonPropertyName("current_version")] public string CurrentVersion { get; set; } = "";
    [JsonPropertyName("latest_version")] public string LatestVersion { get; set; } = "";
    [JsonPropertyName("has_update")] public bool HasUpdate { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
}
