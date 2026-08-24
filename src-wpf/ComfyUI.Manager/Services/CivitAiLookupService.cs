using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 T9a:CivitAI lookup service — 模糊搜索 + 详情获取,供本地模型 sidebar
/// "查询 CivitAI" 按钮使用(T9b 集成 UI)。
///
/// 设计要点:
/// <list type="bullet">
/// <item>Sibling 不是 IModelSource 子类 —— CivitAiModelSource 是 marketplace 聚合
///   用的,有 SearchPageAsync/SearchAsync 协议 + ModelEntry 转换。本 service 只需
///   2 个独立方法,继承 IModelSource 会污染接口。</item>
/// <item>HttpClient/proxy/apiToken 由 caller 注入(同 CivitAiModelSource)—— 不内部
///   创建 HttpClient,避免跟现有 DI 池产生 socket exhaustion。</item>
/// <item>rich Console log 镜像 v0.6.22++(proxy/port/status/bytes/duration),
///   走 AppLogger subsystem="civitai-lookup"。T9b VM 层会包 Progress&lt;string&gt;
///   转推 UI Console panel。</item>
/// </list>
/// </summary>
public sealed class CivitAiLookupService
{
    private const string LogSubsystem = "civitai-lookup";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    private readonly AppLogger? _logger;

    public CivitAiLookupService(
        HttpClient http,
        string baseUrl,
        string apiToken,
        AppLogger? logger = null,
        HttpProxyConfig? proxy = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? "https://civitai.com").TrimEnd('/');
        _apiToken = apiToken ?? "";
        _logger = logger;

        // v0.6.22+ 镜像模式:仅在 HTTPS baseUrl 注入 Bearer(防 token 通过 HTTP 镜像明文泄露)。
        // proxy 参数在 ctor 接收仅为 API 一致性 + 测试 seam —— 实际 proxy 由 caller 在
        // 构造 HttpClient 时通过 HttpClientHandler.ApplyTo 配置(caller 拥有 handler lifecycle)。
        if (!string.IsNullOrEmpty(_apiToken) &&
            _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiToken);
        }
    }

    /// <summary>Fuzzy search by title. Returns 0+ candidates ordered by relevance
    /// (CivitAI default = Most Downloaded)。最多 10 项,enough for user selection。
    /// <exception cref="HttpRequestException">non-2xx response</exception>
    /// <exception cref="OperationCanceledException">ct cancelled</exception>
    /// <exception cref="InvalidOperationException">malformed JSON / null payload</exception>
    /// </summary>
    public async Task<IReadOnlyList<CivitAiCandidate>> SearchByTitleAsync(
        string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<CivitAiCandidate>();

        var url = $"{_baseUrl}/api/v1/models" +
                  $"?query={Uri.EscapeDataString(title)}" +
                  $"&limit=10" +
                  $"&nsfw=true" +
                  $"&sort=MostDownloaded";
        _logger?.Info(LogSubsystem, $"→ {url}");
        var sw = Stopwatch.StartNew();

        try
        {
            var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.Info(LogSubsystem,
                $"← {(int)resp.StatusCode} {resp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"CivitAI returned {(int)resp.StatusCode} {resp.StatusCode}, " +
                    $"body {body.Length} bytes, {sw.ElapsedMilliseconds}ms");
            }

            var dto = JsonSerializer.Deserialize<CivitAiSearchResponse>(body);
            var candidates = dto?.Items?
                .Select(i => new CivitAiCandidate(
                    Id: i.Id ?? 0,
                    Title: i.Name ?? "",
                    Username: i.Creator?.Username ?? "",
                    BaseModel: i.BaseModel,
                    ThumbnailUrl: i.ImageUrl ?? i.Images?.FirstOrDefault()?.Url))
                .ToList() ?? new List<CivitAiCandidate>();
            _logger?.Info(LogSubsystem, $"✓ {candidates.Count} 项");
            return candidates;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger?.Info(LogSubsystem, $"⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
        catch (JsonException ex)
        {
            sw.Stop();
            _logger?.Error(LogSubsystem,
                $"✗ JsonException ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            throw new InvalidOperationException(
                $"CivitAI search response JSON 解析失败: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error(LogSubsystem,
                $"✗ {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            throw;
        }
    }

    /// <summary>Fetch full detail by CivitAI model id (numeric).
    /// <exception cref="CivitAiLookupNotFoundException">404 response</exception>
    /// <exception cref="HttpRequestException">other non-2xx</exception>
    /// <exception cref="OperationCanceledException">ct cancelled</exception>
    /// <exception cref="InvalidOperationException">malformed JSON / null payload</exception>
    /// </summary>
    public async Task<CivitAiDetailDto> GetDetailAsync(int modelId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/models/{modelId}";
        _logger?.Info(LogSubsystem, $"→ {url}");
        var sw = Stopwatch.StartNew();

        try
        {
            var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.Info(LogSubsystem,
                $"← {(int)resp.StatusCode} {resp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new CivitAiLookupNotFoundException(modelId);
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"CivitAI returned {(int)resp.StatusCode} {resp.StatusCode}, " +
                    $"body {body.Length} bytes, {sw.ElapsedMilliseconds}ms");
            }

            var dto = JsonSerializer.Deserialize<CivitAiDetailResponse>(body);
            if (dto is null) throw new InvalidOperationException("CivitAI detail response 为空");

            var versions = (dto.ModelVersions ?? new List<CivitAiVersionWire>())
                .Select(v => new CivitAiVersionDto(
                    Name: v.Name ?? "",
                    BaseModel: v.BaseModel,
                    CreatedAt: v.CreatedAt))
                .ToList();
            var images = (dto.Images ?? new List<CivitAiImageDto>())
                .Select(i => i.Url ?? "")
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            _logger?.Info(LogSubsystem,
                $"✓ 详情: {versions.Count} 个版本, {images.Count} 张图片");
            return new CivitAiDetailDto(
                Id: dto.Id ?? 0,
                Title: dto.Name ?? "",
                Username: dto.Creator?.Username ?? "",
                BaseModel: dto.BaseModel,
                Description: dto.Description ?? "",
                Tags: dto.Tags ?? new List<string>(),
                Versions: versions,
                ImageUrls: images);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger?.Info(LogSubsystem, $"⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
        catch (CivitAiLookupNotFoundException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            sw.Stop();
            _logger?.Error(LogSubsystem,
                $"✗ JsonException ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            throw new InvalidOperationException(
                $"CivitAI detail response JSON 解析失败: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error(LogSubsystem,
                $"✗ {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            throw;
        }
    }

    /// <summary>v1.0.0 T13:Single-model lookup by SHA256 hash via
    /// <c>GET /api/v1/model-versions/by-hash/{hash}</c>. 404 returns null (not throw).
    /// Other non-2xx → null + log. Network/JSON errors → null + log.
    /// <exception cref="OperationCanceledException">ct cancelled</exception>
    /// </summary>
    public async Task<CivitAiDetailDto?> LookupByHashAsync(string sha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var url = $"{_baseUrl}/api/v1/model-versions/by-hash/{sha256}";
        _logger?.Info(LogSubsystem, $"→ {url}");
        var sw = Stopwatch.StartNew();

        try
        {
            var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.Info(LogSubsystem,
                $"← {(int)resp.StatusCode} {resp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.Warn(LogSubsystem,
                    $"✗ {(int)resp.StatusCode} ({sw.ElapsedMilliseconds}ms): by-hash failed");
                return null;
            }

            var dto = JsonSerializer.Deserialize<CivitAiDetailResponse>(body);
            if (dto is null) return null;

            var versions = (dto.ModelVersions ?? new List<CivitAiVersionWire>())
                .Select(v => new CivitAiVersionDto(
                    Name: v.Name ?? "",
                    BaseModel: v.BaseModel,
                    CreatedAt: v.CreatedAt))
                .ToList();
            var images = (dto.Images ?? new List<CivitAiImageDto>())
                .Select(i => i.Url ?? "")
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            return new CivitAiDetailDto(
                Id: dto.Id ?? 0,
                Title: dto.Name ?? "",
                Username: dto.Creator?.Username ?? "",
                BaseModel: dto.BaseModel,
                Description: dto.Description ?? "",
                Tags: dto.Tags ?? new List<string>(),
                Versions: versions,
                ImageUrls: images);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger?.Info(LogSubsystem, $"⏹ 已取消 ({sw.ElapsedMilliseconds}ms)");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error(LogSubsystem,
                $"✗ {ex.GetType().Name} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return null;
        }
    }
}

/// <summary>v1.0.0 T9a:Detail 404 专用 exception,VM 可显式 catch 弹 "未找到"
/// 而非泛 HttpRequestException。</summary>
public sealed class CivitAiLookupNotFoundException : Exception
{
    public int ModelId { get; }
    public CivitAiLookupNotFoundException(int modelId)
        : base($"CivitAI model {modelId} not found")
    {
        ModelId = modelId;
    }
}