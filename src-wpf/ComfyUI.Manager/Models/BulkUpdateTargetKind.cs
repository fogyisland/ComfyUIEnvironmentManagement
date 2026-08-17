namespace ComfyUI.Manager.Models;

public enum BulkUpdateTargetKind
{
    ComfyUi,
    ComfyUiManager,
    /// <summary>
    /// v0.6.18.1:per-node 更新 — git pull &lt;node.PackagePath&gt;。
    /// 配合 <see cref="BulkUpdateRow.NodeId"/> 一起用,NodeId 字段在 Node
    /// 类型的 row 上必填,其它 target 上为 null。
    /// </summary>
    Node,
}