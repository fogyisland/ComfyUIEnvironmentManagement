using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 一行 bulk update 进度 / 结果。
///
/// v0.6.18.1 加 <see cref="NodeId"/> 字段 —
/// <see cref="BulkUpdateTargetKind.Node"/> 类型的 row 用它定位节点目录(走
/// <c>node.PackagePath</c>),其它 target 上为 null。
/// v0.6.18.2 加 <see cref="ItemName"/> 计算属性 —— 右列 DataGrid 不用
/// EnvId + TargetKind 两个字段拼,直接绑 <see cref="ItemName"/> 显示友好名。
/// </summary>
public record BulkUpdateRow(
    string EnvId,
    BulkUpdateTargetKind TargetKind,
    string Status,    // pending | running | succeeded | skipped | failed
    string? Reason,
    [property: JsonPropertyName("latency_ms")] int LatencyMs,
    [property: JsonPropertyName("percent")] double Percent = 0,
    string? NodeId = null)
{
    /// <summary>
    /// v0.6.18.2:UI 显示用 Item 名。Node target 用 NodeId(没有 EnvName 信息,因为
    /// UpdateItem 跨 env 合并时丢了 env 上下文),其它 target 用 EnvId。
    /// </summary>
    [JsonIgnore]
    public string ItemName => TargetKind switch
    {
        BulkUpdateTargetKind.Node => NodeId ?? EnvId,
        BulkUpdateTargetKind.ComfyUi => $"{EnvId} · 基础环境",
        BulkUpdateTargetKind.ComfyUiManager => $"{EnvId} · ComfyUI-Manager",
        _ => EnvId,
    };
}