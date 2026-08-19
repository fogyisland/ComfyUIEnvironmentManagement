using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.21:HuggingFace Hub API fetcher.
/// Search: GET {baseUrl}/api/models?search={q}&limit={n}&full=true
/// Detail: GET {baseUrl}/api/models/{repo_id} (siblings, cardData, tags)
/// Auth: optional Bearer token for higher rate limit + gated models.
/// Kind: tag-based heuristic (lora/checkpoint/vae/...); NSFW: any tag contains "nsfw".</summary>
public class HuggingFaceModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    private readonly AppLogger? _logger;
    private const int PageSize = 50;  // HF default limit per page

    public ModelSourceKind SourceKind => ModelSourceKind.HuggingFace;
    public string DisplayName => "HuggingFace";
    public bool IsEnabled { get; set; } = true;  // factory decides enabled via construction (returns null if disabled)

    public HuggingFaceModelSource(HttpClient http, string baseUrl, string apiToken, AppLogger? logger = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiToken = apiToken ?? "";
        _logger = logger;
        if (_baseUrl != "https://huggingface.co")
        {
            _logger?.Info("model-mirror", $"using HF mirror: {_baseUrl}");
        }
    }

    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var results = new List<ModelEntry>();
        var qs = new List<string>
        {
            $"limit={Math.Min(maxResults, PageSize)}",
            "full=true",
        };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"search={Uri.EscapeDataString(query)}");
        var url = $"{_baseUrl}/api/models?{string.Join("&", qs)}";
        _logger?.Info("model-huggingface", $"search: {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var items = JsonSerializer.Deserialize<List<HfModelSummary>>(body, JsonOpts);
        if (items is null) return results;

        foreach (var item in items.Take(maxResults))
        {
            if (string.IsNullOrEmpty(item.Id)) continue;
            var entry = await MapToModelEntryAsync(item, ct).ConfigureAwait(false);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    private async Task<ModelEntry?> MapToModelEntryAsync(HfModelSummary summary, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/models/{summary.Id}");
            if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
            }
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var detail = JsonSerializer.Deserialize<HfModelDetail>(body, JsonOpts);
            if (detail is null) return null;

            var tags = detail.Tags ?? new List<string>();
            var kind = MapKindFromTags(tags);
            var nsfwKind = tags.Any(t => t.Contains("nsfw", StringComparison.OrdinalIgnoreCase))
                ? ModelNsfwKind.NSFW
                : ModelNsfwKind.SFW;
            var primary = PickPrimaryFile(detail.Siblings, summary.Id, _baseUrl);
            if (primary is null) return null;  // no model files in siblings

            var sha = detail.Sha ?? summary.Id;
            var sha8 = sha.Length >= 8 ? sha.Substring(0, 8) : sha;

            // Construct entry first (without Parent back-ref since Version needs entry ref)
            var entry = new ModelEntry
            {
                Source = ModelSourceKind.HuggingFace,
                SourceId = summary.Id,
                SourceUrl = $"{_baseUrl}/{summary.Id}",
                Title = summary.Id,
                Description = detail.CardData?.Description,
                Author = summary.Id.Contains('/') ? summary.Id.Split('/')[0] : "",
                Kind = kind,
                NsfwKind = nsfwKind,
                PreviewImageUrl = "",
                Tags = (detail.Tags ?? new List<string>()).AsReadOnly(),
                Versions = Array.Empty<ModelVersionEntry>(),
                PublishedAt = detail.LastModified.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(detail.LastModified.Value, DateTimeKind.Utc))
                    : null,
            };

            var version = new ModelVersionEntry
            {
                Id = $"{ModelSourceKind.HuggingFace}:{summary.Id}:{sha8}",
                SourceVersionId = sha,
                Name = detail.Id ?? summary.Id,
                PrimaryDownloadUrl = primary.DownloadUrl,
                SizeBytes = primary.SizeBytes,
                Files = new List<ModelFile> { primary }.AsReadOnly(),
                Parent = entry,
            };

            // Replace entry with one that has the version list containing the back-ref
            var finalEntry = new ModelEntry
            {
                Source = entry.Source,
                SourceId = entry.SourceId,
                SourceUrl = entry.SourceUrl,
                Title = entry.Title,
                Description = entry.Description,
                Author = entry.Author,
                AuthorUrl = entry.AuthorUrl,
                Kind = entry.Kind,
                BaseModel = entry.BaseModel,
                NsfwKind = entry.NsfwKind,
                NsfwLevel = entry.NsfwLevel,
                DownloadCount = entry.DownloadCount,
                RatingCount = entry.RatingCount,
                RatingStars = entry.RatingStars,
                PublishedAt = entry.PublishedAt,
                Tags = entry.Tags,
                PreviewImageUrl = entry.PreviewImageUrl,
                Versions = new List<ModelVersionEntry> { version }.AsReadOnly(),
            };
            return finalEntry;
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-huggingface", $"failed to map {summary.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Priority-order tag → ModelKind mapping. Unknown → Other.</summary>
    internal static ModelKind MapKindFromTags(IList<string> tags)
    {
        if (tags.Any(t => t.Equals("lora", StringComparison.OrdinalIgnoreCase))) return ModelKind.LORA;
        if (tags.Any(t => t.Equals("checkpoint", StringComparison.OrdinalIgnoreCase))) return ModelKind.Checkpoint;
        if (tags.Any(t => t.Equals("vae", StringComparison.OrdinalIgnoreCase))) return ModelKind.VAE;
        if (tags.Any(t => t.Equals("controlnet", StringComparison.OrdinalIgnoreCase))) return ModelKind.Controlnet;
        if (tags.Any(t => t.Equals("textual-inversion", StringComparison.OrdinalIgnoreCase))) return ModelKind.TextualInversion;
        if (tags.Any(t => t.Equals("upscaler", StringComparison.OrdinalIgnoreCase))) return ModelKind.Upscaler;
        if (tags.Any(t => t.Equals("hypernetwork", StringComparison.OrdinalIgnoreCase))) return ModelKind.Hypernetwork;
        return ModelKind.Other;
    }

    /// <summary>Pick largest *.safetensors / *.bin from siblings; fallback to first one if size missing.</summary>
    internal static ModelFile? PickPrimaryFile(IList<HfSibling>? siblings, string repoId, string baseUrl)
    {
        if (siblings is null || siblings.Count == 0) return null;
        var candidates = siblings
            .Where(s => !string.IsNullOrEmpty(s.Rfilename) &&
                       (s.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                        s.Rfilename.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0) return null;
        var withSize = candidates.Where(s => s.Size.HasValue).OrderByDescending(s => s.Size!.Value).FirstOrDefault();
        var pick = withSize ?? candidates.First();
        return new ModelFile
        {
            Name = pick.Rfilename!,
            Format = pick.Rfilename!.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? "Safetensors" : "Other",
            SizeBytes = pick.Size ?? 0,
            DownloadUrl = $"{baseUrl.TrimEnd('/')}/{repoId}/resolve/main/{pick.Rfilename}",
            IsPrimary = true,
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // —— DTOs (internal so PickPrimaryFile's parameter type is accessible) ——
    internal class HfModelSummary
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
    internal class HfModelDetail
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
        [JsonPropertyName("lastModified")] public DateTime? LastModified { get; set; }
        [JsonPropertyName("cardData")] public HfCardData? CardData { get; set; }
    }
    internal class HfSibling
    {
        [JsonPropertyName("rfilename")] public string? Rfilename { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
    internal class HfCardData
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
