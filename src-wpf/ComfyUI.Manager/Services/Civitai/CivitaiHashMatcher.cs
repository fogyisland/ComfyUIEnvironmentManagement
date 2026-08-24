using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Primary matcher — SHA256 hash → /api/v1/model-versions/by-hash/{hash}.
/// Returns null if model.Hash is null (caller didn't compute it) or service returns null.
/// Never throws: orchestrator expects nullable MatchResult, network/404 is the dominant failure mode.
/// </summary>
public sealed class CivitaiHashMatcher : IModelMatcher
{
    private const string LogSubsystem = "civitai-hash-matcher";

    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "Hash";

    public CivitaiHashMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.Hash)) return null;
        var detail = await _service.LookupByHashAsync(model.Hash, ct).ConfigureAwait(false);
        if (detail is null) return null;
        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        _logger?.Info(LogSubsystem, $"hit {model.Hash} → \"{detail.Title}\"");
        return new MatchResult(MatchSource.Hash, detail, cover);
    }
}
