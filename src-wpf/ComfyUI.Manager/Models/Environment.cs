using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public class Environment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "isolated";
    [JsonPropertyName("python_executable")]
    public string PythonExecutable { get; set; } = "";
    [JsonPropertyName("port")]
    public int Port { get; set; }
    [JsonPropertyName("status")]
    public string Status { get; set; } = "stopped";
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }
}
