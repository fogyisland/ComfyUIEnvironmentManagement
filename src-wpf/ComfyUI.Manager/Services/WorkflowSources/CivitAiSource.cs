using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:CivitAI 数据源 — /api/v1/images?tags=workflow 拉图像,
/// 每张图的 metadata.workflow 字段含 workflow JSON URL。
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
        // CivitAI images API:N=limit, tags=workflow filter; sort=Newest 默认
        var url = $"{_baseUrl}/api/v1/images?tags=workflow&sort=Newest&limit={Math.Min(maxResults, 100)}";
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
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                _logger?.Warn("workflow-civitai", "未识别的 JSON shape");
                return Array.Empty<WorkflowEntry>();
            }

            var entries = new List<WorkflowEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : "";
                if (string.IsNullOrEmpty(id)) continue;

                // metadata.workflow 是 CivitAI 的 workflow JSON URL 字段
                string? jsonUrl = null;
                if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("workflow", out var wfProp)
                    && wfProp.ValueKind == JsonValueKind.Object
                    && wfProp.TryGetProperty("workflowJson", out var wjProp))
                {
                    jsonUrl = wjProp.GetString();
                }
                // 部分 CivitAI 图像 metadata 用不同字段名 — 尝试 backup 路径
                if (string.IsNullOrEmpty(jsonUrl) && item.TryGetProperty("url", out var urlProp))
                {
                    jsonUrl = urlProp.GetString();
                }
                if (string.IsNullOrEmpty(jsonUrl)) continue;

                var title = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var author = item.TryGetProperty("username", out var uProp) ? uProp.GetString() : null;
                var previewUrl = item.TryGetProperty("url", out var puProp) ? puProp.GetString() : null;

                // query 过滤(title/author substring)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = title?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = author?.ToLowerInvariant().Contains(q) ?? false;
                    if (!inTitle && !inAuthor) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = id,
                    SourceUrl = $"{_baseUrl}/images/{id}",
                    WorkflowJsonUrl = jsonUrl,
                    PreviewImageUrl = previewUrl,
                    Title = title,
                    Author = author,
                    Tags = Array.Empty<string>(),
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
}