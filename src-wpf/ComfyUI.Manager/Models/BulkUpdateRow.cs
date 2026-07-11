using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

public record BulkUpdateRow(
    string EnvId,
    string NodeId,
    string Status,    // pending | running | succeeded | skipped | failed
    string? Reason,
    int LatencyMs
);
