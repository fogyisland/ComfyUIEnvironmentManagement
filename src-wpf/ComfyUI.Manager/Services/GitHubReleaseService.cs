using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11+ dashboard/splash polish:GitHub Releases API 全 list fetch + 24h cache。
/// v0.6.16:Cache 走 <see cref="LocalDataPaths"/>(默认 &lt;projectRoot&gt;/.manager/release_cache.json;
/// 旧 %APPDATA%/ComfyUI-Manager/release_cache.json 由 LocalDataMigrationService 一次性迁过来)。
/// 网络失败:返 last cached(可空),<see cref="LastSyncUtc"/> 只在成功 sync / cache hit 时才有值。
/// cache 文件损坏(非法 JSON)会抛 <see cref="JsonException"/> — 上层应视为可诊断的配置错误。
/// </summary>
public sealed class GitHubReleaseService
{
    private const string RepoOwner = "fogyisland";
    private const string RepoName = "ComfyUIEnvironmentManagement";
    private const string ApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions ReadOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly string _cacheFilePath;
    private readonly TimeSpan _cacheTtl;

    /// <summary>
    /// 生产 DI 入口 —— 接受 <see cref="LocalDataPaths"/> 提供 cache 路径。
    /// </summary>
    public GitHubReleaseService(
        HttpClient http,
        LocalDataPaths paths,
        AppLogger? logger = null,
        TimeSpan? cacheTtl = null)
    {
        _http = http;
        _logger = logger;
        _cacheFilePath = paths.ReleaseCacheFile;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(24);
    }

    /// <summary>
    /// 测试 seam —— 显式传入 cache 路径。生产代码走 LocalDataPaths ctor。
    /// </summary>
    public GitHubReleaseService(
        HttpClient http,
        AppLogger? logger = null,
        string cacheFilePath = default!,
        TimeSpan? cacheTtl = null)
    {
        _http = http;
        _logger = logger;
        _cacheFilePath = cacheFilePath;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(24);
    }

    public DateTime? LastSyncUtc { get; private set; }

    public async Task<IReadOnlyList<GitHubRelease>> FetchAsync(
        CancellationToken ct = default)
    {
        // 1. 检查 cache:如果 cache 写入时间在 TTL 内,直接返
        var cached = TryReadCache();
        if (cached is not null
            && cached.SyncedAt > DateTime.UtcNow - _cacheTtl)
        {
            LastSyncUtc = cached.SyncedAt;
            return cached.Releases;
        }

        // 2. 网络 fetch
        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("ComfyUI-Manager");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, ReadOptions)
                ?? new List<GitHubRelease>();

            // 3. 写入 cache
            var now = DateTime.UtcNow;
            TryWriteCache(new CacheEnvelope(now, releases));
            LastSyncUtc = now;
            return releases;
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-fetch",
                $"fetch failed, returning last cached: {ex.Message}");
            // 4. 网络失败:返 last cached(可能空)
            if (cached is not null)
            {
                LastSyncUtc = cached.SyncedAt;
                return cached.Releases;
            }
            LastSyncUtc = null;
            return Array.Empty<GitHubRelease>();
        }
    }

    // ---- cache helpers ----

    private CacheEnvelope? TryReadCache()
    {
        string json;
        try
        {
            if (!File.Exists(_cacheFilePath)) return null;
            json = File.ReadAllText(_cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-cache",
                $"cache read failed: {ex.Message}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CacheEnvelope>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            // cache 文件内容损坏 —— 记日志后往上抛,让调用方能诊断而不是静默降级。
            _logger?.Warn("github-release-cache",
                $"cache parse failed (corrupt file {_cacheFilePath}): {ex.Message}");
            throw;
        }
    }

    private void TryWriteCache(CacheEnvelope env)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(env,
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch (Exception ex)
        {
            _logger?.Warn("github-release-cache",
                $"cache write failed: {ex.Message}");
        }
    }

    /// <summary>测试 seam:序列化 cache envelope 供 test 预填文件用。</summary>
    public static string SerializeCache(IReadOnlyList<GitHubRelease> releases,
        DateTime syncedAt) =>
        JsonSerializer.Serialize(new CacheEnvelope(syncedAt, releases),
            new JsonSerializerOptions { WriteIndented = false });

    private sealed record CacheEnvelope(
        [property: JsonPropertyName("syncedAt")] DateTime SyncedAt,
        [property: JsonPropertyName("releases")] IReadOnlyList<GitHubRelease> Releases);
}
