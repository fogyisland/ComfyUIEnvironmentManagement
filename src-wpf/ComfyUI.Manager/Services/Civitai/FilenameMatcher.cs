using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Last-resort fallback — fuzzy-search CivitAI by the local model's
/// Title (PrettyPrint of filename). Picks first candidate. This is the original T11 behavior.</summary>
public sealed class FilenameMatcher : IModelMatcher
{
    private const string LogSubsystem = "civitai-filename-matcher";

    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "Filename";

    public FilenameMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.Title)) return null;
        var candidates = await _service.SearchByTitleAsync(model.Title, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;
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
        _logger?.Info(LogSubsystem, $"\"{model.Title}\" → \"{detail.Title}\" (id={detail.Id})");
        return new MatchResult(MatchSource.FilenameFuzzy, detail, cover);
    }
}
