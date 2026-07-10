using System.Text.Json.Serialization;
namespace ComfyUI.Manager.Models;

public class CatalogEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("stars")]
    public int Stars { get; set; }
    [JsonPropertyName("author")]
    public string? Author { get; set; }
    [JsonPropertyName("stale")]
    public bool Stale { get; set; }
}