using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public record BulkUpdateRow(
    string EnvId,
    string NodeId,
    string Status,    // pending | running | succeeded | skipped | failed
    string? Reason,
    [property: JsonPropertyName("latency_ms")] int LatencyMs
);