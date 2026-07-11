using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

public class BulkUpdateApiClient : ApiClient
{
    public BulkUpdateApiClient(string baseUrl) : base(baseUrl) { }

    public virtual Task<ErrorEnvelope<BulkUpdateStartedResponse>> StartAsync(
        List<string> envIds, List<string> nodeIds, CancellationToken ct = default)
    {
        var body = new { env_ids = envIds, node_ids = nodeIds };
        return PostAsync<BulkUpdateStartedResponse>("bulk-update/start", body, ct);
    }

    public virtual Task<ErrorEnvelope<BulkUpdateCancelledResponse>> CancelAsync(
        string bulkId, CancellationToken ct = default)
    {
        return PostAsync<BulkUpdateCancelledResponse>($"bulk-update/{bulkId}/cancel",
            new { }, ct);
    }

    public virtual Task<ErrorEnvelope<BulkUpdateStatus>> GetStatusAsync(
        string bulkId, CancellationToken ct = default)
    {
        return GetAsync<BulkUpdateStatus>($"bulk-update/{bulkId}", ct);
    }
}
