using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Fallback matcher — reads .civitai.info sidecar JSON next to the model
/// file (Civitai Helper convention). Extracts modelId and calls GetDetailAsync.
/// Returns null if sidecar missing, malformed, or detail returns 404.</summary>
public sealed class CompanionJsonMatcher : IModelMatcher
{
    private const string LogSubsystem = "civitai-companion-matcher";

    private readonly CivitAiLookupService _service;
    private readonly AppLogger? _logger;

    public string Name => "CompanionJson";

    public CompanionJsonMatcher(CivitAiLookupService service, AppLogger? logger = null)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.FullPath)) return null;
        var dir = Path.GetDirectoryName(model.FullPath);
        var basename = Path.GetFileNameWithoutExtension(model.FullPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename)) return null;

        var sidecarPath = Path.Combine(dir, $"{basename}.civitai.info");
        if (!File.Exists(sidecarPath)) return null;

        int? modelId = null;
        try
        {
            var json = File.ReadAllText(sidecarPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("modelId", out var mid)
                && mid.TryGetInt32(out var midVal))
            {
                modelId = midVal;
            }
        }
        catch { return null; }
        if (modelId is null) return null;

        CivitAiDetailDto? detail;
        try
        {
            detail = await _service.GetDetailAsync(modelId.Value, ct).ConfigureAwait(false);
        }
        catch (CivitAiLookupNotFoundException) { return null; }
        catch { return null; }
        if (detail is null) return null;

        var cover = detail.ImageUrls.Count > 0 ? detail.ImageUrls[0] : null;
        _logger?.Info(LogSubsystem, $"sidecar {sidecarPath} → \"{detail.Title}\" (id={detail.Id})");
        return new MatchResult(MatchSource.CompanionJson, detail, cover);
    }
}
