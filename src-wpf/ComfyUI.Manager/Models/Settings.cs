using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public class Settings
{
    [JsonPropertyName("theme")] public string Theme { get; set; } = "material_purple";
    [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "system";
    [JsonPropertyName("language")] public string Language { get; set; } = "zh_CN";
    [JsonPropertyName("catalog_auto_refresh")] public bool CatalogAutoRefresh { get; set; }
    [JsonPropertyName("catalog_cache_ttl_minutes")] public int CatalogCacheTtlMinutes { get; set; } = 60;
    [JsonPropertyName("compat_api_base_url")] public string CompatApiBaseUrl { get; set; } = "";
}
