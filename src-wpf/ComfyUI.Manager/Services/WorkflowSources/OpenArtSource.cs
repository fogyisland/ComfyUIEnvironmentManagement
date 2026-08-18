using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:OpenArt 数据源 — 通用 JSON list 端点(镜像 CommunityJsonSource)。
/// 期望响应:{"items":[{"id":"...","title":"...","author":"...","json_url":"...",
///                    "preview_url":"...","tags":[...]}]}
/// 端点 URL 在 ctor 注入,默认 placeholder(实现时改成真 endpoint)。</summary>
public class OpenArtSource : IWorkflowSource
{
    public WorkflowSourceKind SourceKind => WorkflowSourceKind.OpenArt;
    public string DisplayName => "OpenArt";
    public bool IsEnabled { get; set; } = true;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly AppLogger? _logger;

    public OpenArtSource(HttpClient http, AppLogger? logger = null, string? url = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        // 真 endpoint 在 implementation 时确认;placeholder 便于测试用 DelegatingHandler 拦截
        _url = url ?? "https://example.com/openart-workflows.json";
    }

    public virtual async Task<IReadOnlyList<WorkflowEntry>> SearchAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        _logger?.Info("workflow-openart", $"fetch url={_url} query='{query}'");
        try
        {
            using var resp = await _http.GetAsync(_url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            // 兼容两种 shape:{items:[...]} 或 顶层 array
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out items))
            {
                // ok
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                items = root;
            }
            else
            {
                _logger?.Warn("workflow-openart",
                    $"未识别的 JSON shape (root={root.ValueKind})");
                return Array.Empty<WorkflowEntry>();
            }

            var entries = new List<WorkflowEntry>();
            foreach (var el in items.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;
                var title = el.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                var jsonUrl = el.TryGetProperty("json_url", out var jProp) ? jProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(jsonUrl)) continue;

                // query 过滤(简单 title/author substring)
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var q = query.ToLowerInvariant();
                    var inTitle = title?.ToLowerInvariant().Contains(q) ?? false;
                    var inAuthor = el.TryGetProperty("author", out var aProp) &&
                        (aProp.GetString()?.ToLowerInvariant().Contains(q) ?? false);
                    if (!inTitle && !inAuthor) continue;
                }

                entries.Add(new WorkflowEntry
                {
                    Source = SourceKind,
                    SourceId = id,
                    SourceUrl = _url,
                    WorkflowJsonUrl = jsonUrl,
                    PreviewImageUrl = el.TryGetProperty("preview_url", out var pProp) ? pProp.GetString() : null,
                    Title = title,
                    Description = el.TryGetProperty("description", out var dProp) ? dProp.GetString() : null,
                    Author = el.TryGetProperty("author", out var auProp) ? auProp.GetString() : null,
                    DownloadCount = el.TryGetProperty("downloads", out var dlProp) && dlProp.TryGetInt32(out var dl) ? dl : null,
                    PublishedAt = el.TryGetProperty("published_at", out var paProp) && DateTimeOffset.TryParse(paProp.GetString(), out var pa) ? pa : null,
                    Tags = el.TryGetProperty("tags", out var tgProp) && tgProp.ValueKind == JsonValueKind.Array
                        ? tgProp.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => !string.IsNullOrEmpty(t)).ToArray()
                        : Array.Empty<string>(),
                });

                if (entries.Count >= maxResults) break;
            }

            _logger?.Info("workflow-openart",
                $"fetched {entries.Count} entries (max={maxResults})");
            return entries;
        }
        catch (Exception ex)
        {
            _logger?.Error("workflow-openart", $"fetch failed url={_url}", ex);
            return Array.Empty<WorkflowEntry>();
        }
    }
}