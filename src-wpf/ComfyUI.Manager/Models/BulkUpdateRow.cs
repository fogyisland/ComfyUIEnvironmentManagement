using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 一行 bulk update 进度 / 结果。
///
/// v0.6.18.1 加 <see cref="NodeId"/> 字段 —
/// <see cref="BulkUpdateTargetKind.Node"/> 类型的 row 用它定位节点目录(走
/// <c>node.PackagePath</c>),其它 target 上为 null。
/// </summary>
public record BulkUpdateRow(
    string EnvId,
    BulkUpdateTargetKind TargetKind,
    string Status,    // pending | running | succeeded | skipped | failed
    string? Reason,
    [property: JsonPropertyName("latency_ms")] int LatencyMs,
    [property: JsonPropertyName("percent")] double Percent = 0,
    string? NodeId = null
);