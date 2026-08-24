using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Fallback matcher — reads .safetensors header for embedded model name,
/// then fuzzy-searches CivitAI by that name. Picks first candidate (single result preferred).</summary>
public sealed class SafetensorsMetadataMatcher : IModelMatcher
{
    private const string LogSubsystem = "civitai-safetensors-matcher";

    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "SafetensorsMetadata";

    public SafetensorsMetadataMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (!SafetensorsHeaderReader.TryReadModelName(model.FullPath, out var name)) return null;
        if (string.IsNullOrEmpty(name)) return null;

        var candidates = await _service.SearchByTitleAsync(name, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;

        // Pick first candidate; fetch full detail
        var first = candidates[0];
        CivitAiDetailDto? detail;
        try
        {
            detail = await _service.GetDetailAsync(first.Id, ct).ConfigureAwait(false);
        }
        catch (CivitAiLookupNotFoundException) { return null; }
        catch { return null; }

        if (detail is null) return null;
        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        _logger?.Info(LogSubsystem, $"\"{name}\" → \"{detail.Title}\"");
        return new MatchResult(MatchSource.SafetensorsMetadata, detail, cover);
    }
}
