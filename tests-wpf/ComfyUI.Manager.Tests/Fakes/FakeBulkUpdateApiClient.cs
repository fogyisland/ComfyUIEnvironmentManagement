using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Tests.Fakes;

public class FakeBulkUpdateApiClient : BulkUpdateApiClient
{
    public BulkUpdateStartedResponse? LastStartResponse { get; set; }
    public BulkUpdateStatus? StatusResult { get; set; }
    public string? NextErrorCode { get; set; }

    public FakeBulkUpdateApiClient() : base("http://test/") { }

    public override Task<ErrorEnvelope<BulkUpdateStartedResponse>> StartAsync(
        List<string> envIds, List<string> nodeIds, CancellationToken ct = default)
    {
        if (NextErrorCode != null)
        {
            return Task.FromResult(new ErrorEnvelope<BulkUpdateStartedResponse>
            {
                Ok = false,
                Error = new ErrorBody { Code = NextErrorCode, Message = "fake" },
            });
        }
        LastStartResponse ??= new BulkUpdateStartedResponse("fake-bulk-id");
        return Task.FromResult(new ErrorEnvelope<BulkUpdateStartedResponse>
        {
            Ok = true,
            Value = LastStartResponse,
        });
    }

    public override Task<ErrorEnvelope<BulkUpdateCancelledResponse>> CancelAsync(
        string bulkId, CancellationToken ct = default)
    {
        if (NextErrorCode != null)
        {
            return Task.FromResult(new ErrorEnvelope<BulkUpdateCancelledResponse>
            {
                Ok = false,
                Error = new ErrorBody { Code = NextErrorCode, Message = "fake" },
            });
        }
        return Task.FromResult(new ErrorEnvelope<BulkUpdateCancelledResponse>
        {
            Ok = true,
            Value = new BulkUpdateCancelledResponse("checkpoint-1"),
        });
    }

    public override Task<ErrorEnvelope<BulkUpdateStatus>> GetStatusAsync(
        string bulkId, CancellationToken ct = default)
    {
        if (NextErrorCode != null)
        {
            return Task.FromResult(new ErrorEnvelope<BulkUpdateStatus>
            {
                Ok = false,
                Error = new ErrorBody { Code = NextErrorCode, Message = "fake" },
            });
        }
        return Task.FromResult(new ErrorEnvelope<BulkUpdateStatus>
        {
            Ok = true,
            Value = StatusResult ?? new BulkUpdateStatus(
                bulkId, "completed", "2026-07-11T08:00:00",
                "2026-07-11T08:01:00", 6, 6, 0, 0, null),
        });
    }
}