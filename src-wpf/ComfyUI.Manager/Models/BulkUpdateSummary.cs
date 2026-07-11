using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

public record BulkUpdateSummary(
    int Total,
    int Succeeded,
    int Skipped,
    int Failed,
    List<BulkUpdateRow> Rows
);

public record BulkUpdateStatus(
    string BulkId,
    string Status,         // pending | running | completed | cancelled | failed
    string? StartedAt,
    string? FinishedAt,
    int Total,
    int Succeeded,
    int Skipped,
    int Failed,
    string? Current
)
{
    public bool IsRunning => Status == "running" || Status == "pending";
}

public record BulkUpdateStartedResponse(string BulkId);

public record BulkUpdateCancelledResponse(string CancelledAtCheckpoint);
