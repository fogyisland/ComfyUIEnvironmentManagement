using System;
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
/// 实现:
/// - Host 来自 Settings.NodelistHostKind:
///   - GitHub → https://api.github.com
///   - Custom → Settings.NodelistCustomHostUrl
/// - Token 来自 Settings.NodelistHostToken,走 Authorization: Bearer {token} Header
/// - Path: GET {host}/api/v1/repos/{author}/{repo}
///   - GitHub:返回完整 repo JSON(描述/星数/license/default_branch/updated_at 等)
///   - 自定义 host:可能不遵循 GitHub v3 API,容忍性解析(描述/更新时间尽力取)
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

    public NodeRepoQueryService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// GET {host}/api/v1/repos/{owner}/{repo} 拿 metadata。
    /// </summary>
    public async Task<RepoMetadata?> FetchRepoMetadataAsync(
        string host, string? token, string owner, string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }
        var url = $"{host.TrimEnd('/')}/api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("ComfyUIManager-NodeRepoQuery/1.0");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var raw = await resp.Content.ReadAsStringAsync(ct);
            return Parse(owner, repo, host, raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 GitHub v3 /api/v1/repos response。Custom host 可能不遵循完整 schema,
    /// 容错解析(字段缺失返 null)。
    /// </summary>
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
