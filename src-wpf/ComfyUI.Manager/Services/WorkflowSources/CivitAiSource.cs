using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.22:CivitAI 数据源 — /api/v1/models?types=WORKFLOW 拉 model entries,
/// 每个 model 含 modelVersions[].files[].json (workflow JSON 文件 URL)。
/// CivitAI 60/h 无 token 限流;Settings 关掉就跳过。</summary>
public class CivitAiSource : IWorkflowSource
{
    public WorkflowSourceKind SourceKind => WorkflowSourceKind.CivitAi;
    public string DisplayName => "CivitAI";
    public bool IsEnabled { get; set; } = true;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly AppLogger? _logger;

    public CivitAiSource(HttpClient http, AppLogger? logger = null, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        _baseUrl = (baseUrl ?? "https://civitai.com").TrimEnd('/');
    }

    public virtual async Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        // v0.6.22: model-centric endpoint — returns proper workflow entries with
        // modelVersions[].files[].json.downloadUrl. Replaces v0.6.19 image-source
        // endpoint which silently dropped entries missing meta.workflow.workflowJson.
        var url = $"{_baseUrl}/api/v1/models?types=WORKFLOW&sort=models.donated&limit={Math.Min(maxResults, 100)}";
        _logger?.Info("workflow-civitai", $"fetch url={url} query='{query}'");
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger?.Warn("workflow-civitai", "rate limited (429)");
                return Array.Empty<WorkflowEntry>();
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = System.Text.Json.JsonSerializer.Deserialize<CivitAiModelResponse>(json)
                       ?? new CivitAiModelResponse();

            var entries = new List<WorkflowEntry>();
            foreach (var item in root.Items)
            {
                if (string.IsNullOrEmpty(item.Name)) continue;

                // v0.6.22: pick first .json from modelVersions[0].files[]
                // (workflow JSON file — typically named workflow.json or <slug>.json).
                // Skip entry if no .json file or empty downloadUrl.
                CivitAiModelFile? jsonFile = null;
                if (item.ModelVersions.Count > 0)
                    jsonFile = PickWorkflowJsonFile(item.ModelVersions[0].Files);
                if (jsonFile is null || string.IsNullOrEmpty(jsonFile.DownloadUrl)) continue;

                var previewUrl = item.ModelVersions.Count > 0 && item.ModelVersions[0].Images.Count > 0
                    ? item.ModelVersions[0].Images[0].Url
                    : null;

                var tags = item.Tags ?? new List<string>();

                // v0.6.22: query filter — title/author/tag substring (case-insensitive)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = item.Name?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = item.Creator?.Username?.ToLowerInvariant().Contains(q) ?? false;
                    var inTag = tags.Any(t => t?.ToLowerInvariant().Contains(q) ?? false);
                    if (!inTitle && !inAuthor && !inTag) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = item.Id.ToString(),
                    SourceUrl = $"{_baseUrl}/models/{item.Id}",
                    WorkflowJsonUrl = jsonFile.DownloadUrl!,
                    PreviewImageUrl = previewUrl,
                    Title = item.Name,
                    Author = item.Creator?.Username ?? "",
                    Tags = tags.ToArray(),
                });
                if (entries.Count >= maxResults) break;
            }

            _logger?.Info("workflow-civitai", $"fetched {entries.Count} entries");
            return entries;
        }
        catch (Exception ex)
        {
            _logger?.Error("workflow-civitai", "fetch failed", ex);
            return Array.Empty<WorkflowEntry>();
        }
    }

    /// <summary>v0.6.22: file-selection helper — pick first .json file by case-insensitive
    /// extension match. Returns null if no .json file found.</summary>
    private static CivitAiModelFile? PickWorkflowJsonFile(IEnumerable<CivitAiModelFile> files)
    {
        return files.FirstOrDefault(f =>
            !string.IsNullOrEmpty(f.DownloadUrl) &&
            f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }
}

// —— v0.6.22 internal DTOs for /api/v1/models?types=WORKFLOW response ——

internal sealed class CivitAiModelResponse
{
    [JsonPropertyName("items")]
    public List<CivitAiModelItem> Items { get; set; } = new();
}

internal sealed class CivitAiModelItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("creator")] public CivitAiCreator Creator { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("modelVersions")] public List<CivitAiModelVersion> ModelVersions { get; set; } = new();
}

internal sealed class CivitAiCreator
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
}

internal sealed class CivitAiModelVersion
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("files")] public List<CivitAiModelFile> Files { get; set; } = new();
    [JsonPropertyName("images")] public List<CivitAiModelImage> Images { get; set; } = new();
}

internal sealed class CivitAiModelFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
}

internal sealed class CivitAiModelImage
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}