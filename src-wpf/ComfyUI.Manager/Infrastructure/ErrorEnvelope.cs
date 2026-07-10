using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Infrastructure;

public class ErrorEnvelope<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    [JsonPropertyName("value")]
    public T? Value { get; set; }
    [JsonPropertyName("error")]
    public ErrorBody? Error { get; set; }
}

public class ErrorEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    [JsonPropertyName("value")]
    public System.Text.Json.JsonElement? Value { get; set; }
    [JsonPropertyName("error")]
    public ErrorBody? Error { get; set; }
}

public class ErrorBody
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    [JsonPropertyName("detail")]
    public System.Text.Json.JsonElement? Detail { get; set; }
}
