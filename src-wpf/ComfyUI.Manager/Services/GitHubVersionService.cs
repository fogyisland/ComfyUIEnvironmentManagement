using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// GitHubVersionService:批量从 GitHub Releases API 拉取每个节点的历史
/// release 列表(默认最近 10 个),用于在详情面板展示当前版本和历史。
///
/// 关键约束:
/// - 鉴权 token:从 settings.GitHubToken 读,空则走未鉴权(60/h 限流)
/// - 并发 ~10(限流安全 + 速度合理)
/// - 非 GitHub URL 跳过(null entry,不报错)
/// - 单个 repo 失败不影响其他
/// - 支持 CancellationToken,可被 UI 取消
/// </summary>
public class GitHubVersionService
{
    private static readonly Regex GitHubRepoRegex = new(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int Concurrency = 10;
    public const int MaxVersionsPerRepo = 10;

    private readonly HttpClient _http;

    public GitHubVersionService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 旧单条 API(详情面板需要时调)。返回最新 release 的 tag。
    /// </summary>
    public virtual async Task<string?> GetLatestVersionAsync(
        string referenceUrl,
        string? token,
        CancellationToken ct = default)
    {
        var versions = await GetReleasesAsync(referenceUrl, token, ct);
        var first = versions.FirstOrDefault(v => !v.IsPrerelease) ?? versions.FirstOrDefault();
        return first?.Tag;
    }

    /// <summary>
    /// 批量:输入每个节点的 (id, referenceUrl),返回 (id → 版本列表,按
    /// published_at 倒序,最多 10 个)。没解析出的 / 失败的 → 不出现。
    /// </summary>
    public virtual async Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
        IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
        string? token,
        IProgress<VersionFetchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<VersionInfo>>();
        var total = nodes.Count;
        var completed = 0;

        using var sem = new SemaphoreSlim(Concurrency);
        var tasks = nodes.Select(async node =>
        {
            await sem.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                var releases = await GetReleasesAsync(node.ReferenceUrl, token, ct);
                if (releases.Count > 0)
                {
                    lock (result) { result[node.Id] = releases; }
                }
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new VersionFetchProgress(done, total, node.Id));
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>
    /// (owner, repo) — 非 GitHub URL → (null, null)
    /// </summary>
    public static (string? Owner, string? Repo) ParseRepo(string? referenceUrl)
    {
        if (string.IsNullOrWhiteSpace(referenceUrl)) return (null, null);
        var m = GitHubRepoRegex.Match(referenceUrl.Trim());
        if (!m.Success) return (null, null);
        return (m.Groups["owner"].Value, m.Groups["repo"].Value);
    }

    /// <summary>
    /// 拉单个 repo 的 release 列表(/releases?per_page=10)。返回按
    /// published_at 倒序,最多 10 个。draft / 未来 release 会被 API 自然
    /// 过滤掉。
    /// </summary>
    private async Task<List<VersionInfo>> GetReleasesAsync(
        string referenceUrl, string? token, CancellationToken ct)
    {
        var (owner, repo) = ParseRepo(referenceUrl);
        if (owner is null || repo is null) return new List<VersionInfo>();

        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={MaxVersionsPerRepo}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ComfyUI-Manager-WPF");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", token);
            }
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new List<VersionInfo>();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<VersionInfo>();

            var list = new List<VersionInfo>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= MaxVersionsPerRepo) break;
                var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(tag)) continue;
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                var published = rel.TryGetProperty("published_at", out var p) ? p.GetString() : "";
                var prerelease = rel.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
                list.Add(new VersionInfo
                {
                    Tag = tag!,
                    PublishedAt = published ?? "",
                    IsPrerelease = prerelease,
                });
            }
            // published_at 倒序(API 一般已排序,这里兜底)
            list.Sort((a, b) => string.Compare(b.PublishedAt, a.PublishedAt, StringComparison.Ordinal));
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new List<VersionInfo>();
        }
    }
}

public record VersionFetchProgress(int Completed, int Total, string CurrentNodeId);
