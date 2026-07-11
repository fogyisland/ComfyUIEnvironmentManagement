using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public record BulkUpdateSummary(
    int Total,
    int Succeeded,
    int Skipped,
    int Failed,
    List<BulkUpdateRow> Rows
);

public record BulkUpdateStatus(
    [property: JsonPropertyName("bulk_id")] string BulkId,
    string Status,         // pending | running | completed | cancelled | failed
    [property: JsonPropertyName("started_at")] string? StartedAt,
    [property: JsonPropertyName("finished_at")] string? FinishedAt,
    int Total,
    int Succeeded,
    int Skipped,
    int Failed,
    string? Current
)
{
    public bool IsRunning => Status == "running" || Status == "pending";
}

public record BulkUpdateStartedResponse(
    [property: JsonPropertyName("bulk_id")] string BulkId
);

public record BulkUpdateCancelledResponse(
    [property: JsonPropertyName("cancelled_at_checkpoint")] string CancelledAtCheckpoint
);