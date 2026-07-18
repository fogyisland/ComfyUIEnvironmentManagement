using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// NodeSource: a single entry in the user-managed query/download source list.
/// Used as both the catalog JSON source URL (query) and the git clone base URL
/// (download, may contain a <c>{node}</c> placeholder).
/// </summary>
public class NodeSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")]  public string Url  { get; set; } = "";
}
