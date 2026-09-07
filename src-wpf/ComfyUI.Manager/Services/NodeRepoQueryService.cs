using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-directory:云端节点仓库查询。
///
/// 用户原话"基于 nodeJson 文件向云端提交查询请求,提交的信息采用
/// 直接域名 https://your-host/api/v1/repos/torvalds(作者)/linux(节点名),
/// 其中这里的 Your-host 是在设置中进行 2 选 1,采用下拉菜单:
/// 第一个直接选择 github,可以添加源,其他源,TOken"。
///
/// 实施:
/// - Host 来自 Settings.NodelistHostKind:
///   - GitHub → https://api.github.com
///   - Custom → Settings.NodelistCustomHostUrl
/// - Token 走 X-API-Key Header(用户新规范,不是 Authorization: Bearer)
/// - Path: GET {host}/api/v1/repos/{author}/{repo}
/// - 缓存命中复用,未命中排队刷新同步等待
/// - 限流:每 key 50,000 / 小时(用户新规范)
/// - 返回 RepoMetadata 记录
/// </summary>
public sealed class NodeRepoQueryService
{
    public sealed record RepoMetadata(
        string Owner,
        string Repo,
        string? Description,
        int? Stars,
        int? Watchers,
        string? License,
        string? DefaultBranch,
        DateTime? UpdatedAt,
        string RawJson,
        string Host);

    private readonly HttpClient _http;
    // 缓存 key = (host, owner, repo); value = metadata or null(404)
    private readonly ConcurrentDictionary<string, RepoMetadata?> _cache = new();

    // 限流:每 host 50,000 / 小时(简化 per-host counter 而非 per-key)
    private static readonly ConcurrentDictionary<string, DateTime> _lastReset = new();
    private static readonly ConcurrentDictionary<string, int> _counter = new();
    private const int HourlyLimit = 50000;

    public NodeRepoQueryService(HttpClient http)
    {
        _http = http;
    }

    private static string CacheKey(string host, string owner, string repo)
        => $"{host.TrimEnd('/')}|{owner.ToLowerInvariant()}|{repo.ToLowerInvariant()}";

    /// <summary>
    /// GET {host}/api/v1/repos/{owner}/{repo} 拿 metadata(X-API-Key header)。
    /// 缓存命中复用;未命中排队 fetch(同一 host 串行避免限流)。
    /// </summary>
    public async Task<RepoMetadata?> FetchRepoMetadataAsync(
        string host, string? apiKey, string owner, string repo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }
        var key = CacheKey(host, owner, repo);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;  // 命中(也含 null = 404 缓存避免重复 404)
        }
        // 同一 host 串行(避免限流)
        var gate = _hostGates.GetOrAdd(host.TrimEnd('/'), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 双重检查(其他 awaiter 拿过)
            if (_cache.TryGetValue(key, out cached)) return cached;

            if (!CheckRateLimit(host)) return null;

            var url = $"{host.TrimEnd('/')}/api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd("ComfyUIManager-NodeRepoQuery/1.0");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Add("X-API-Key", apiKey.Trim());
            }
            try
            {
                using var resp = await _http.SendAsync(req, ct);
                _counter.AddOrUpdate(host.TrimEnd('/'), 1, (_, n) => n + 1);
                if (!resp.IsSuccessStatusCode)
                {
                    // 404 缓存 null 避免重复 404
                    _cache[key] = null;
                    return null;
                }
                var raw = await resp.Content.ReadAsStringAsync(ct);
                var meta = Parse(owner, repo, host, raw);
                _cache[key] = meta;
                return meta;
            }
            catch
            {
                // 异常不缓存
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // 简易限流(每 host 50000/小时,简单计数 + 1小时 reset)
    private static bool CheckRateLimit(string host)
    {
        var h = host.TrimEnd('/');
        var now = DateTime.UtcNow;
        if (_lastReset.TryGetValue(h, out var last) && (now - last).TotalHours < 1)
        {
            var count = _counter.GetOrAdd(h, 0);
            return count < HourlyLimit;
        }
        _lastReset[h] = now;
        _counter[h] = 0;
        return true;
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();

    private static RepoMetadata? Parse(string owner, string repo, string host, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var desc = TryGetString(root, "description");
            var stars = TryGetInt(root, "stargazers_count");
            var watchers = TryGetInt(root, "subscribers_count") ?? TryGetInt(root, "watchers_count");
            var license = root.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object
                ? TryGetString(lic, "spdx_id") ?? TryGetString(lic, "name")
                : null;
            var defaultBranch = TryGetString(root, "default_branch");
            DateTime? updated = null;
            var updatedStr = TryGetString(root, "updated_at");
            if (!string.IsNullOrEmpty(updatedStr) && DateTime.TryParse(updatedStr, out var dt))
            {
                updated = dt;
            }
            return new RepoMetadata(owner, repo, desc, stars, watchers, license, defaultBranch, updated, raw, host);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
        return null;
    }
}
