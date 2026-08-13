using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    /// 单条场景:撞 rate limit 仍然 fail-fast 抛 RateLimitException(没有
    /// partial result 概念,调用方应该 catch)。
    /// </summary>
    public virtual async Task<string?> GetLatestVersionAsync(
        string referenceUrl,
        string? token,
        CancellationToken ct = default)
    {
        var (releases, header) = await GetReleasesWithRateLimitAsync(referenceUrl, token, ct);
        if (header.RateLimitHit)
        {
            throw new RateLimitException();
        }
        var first = releases.FirstOrDefault(v => !v.IsPrerelease) ?? releases.FirstOrDefault();
        return first?.Tag;
    }

    /// <summary>
    /// 批量:输入每个节点的 (id, referenceUrl),返回 (id → 版本列表,按
    /// published_at 倒序,最多 10 个)。没解析出的 / 失败的 → 不出现。
    ///
    /// v0.6.14.1 hotfix:rate limit 时**不抛** `RateLimitException`,而是
    /// 立即停止后续请求 + return 当前 partial result + log Warn。旧实现
    /// `Task.WhenAll` 在第一个 rate limit 时 aggregate throw,前面已经
    /// lock 写入的 result 全部丢失,下次 refresh 还会撞同样 5000+ entries
    /// 死循环。partial 落库后下次 refresh hash-diff 短路,自然恢复。
    /// </summary>
    public virtual async Task<Dictionary<string, List<VersionInfo>>> FetchVersionsAsync(
        IReadOnlyList<(string Id, string ReferenceUrl)> nodes,
        string? token,
        IProgress<VersionFetchProgress>? progress = null,
        AppLogger? logger = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<VersionInfo>>();
        var total = nodes.Count;
        var completed = 0;
        var rateLimitHit = false;
        long? resetUnix = null;
        long? remaining = null;

        using var sem = new SemaphoreSlim(Concurrency);
        var tasks = nodes.Select(async node =>
        {
            await sem.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                var (releases, headerInfo) = await GetReleasesWithRateLimitAsync(
                    node.ReferenceUrl, token, ct);
                if (headerInfo.RateLimitRemaining is not null)
                {
                    remaining = headerInfo.RateLimitRemaining;
                }
                if (headerInfo.RateLimitReset is not null)
                {
                    resetUnix = headerInfo.RateLimitReset;
                }
                if (headerInfo.RateLimitHit)
                {
                    // 第一个撞 rate limit 的 task 设标志,后续 task 看到标志直接退出
                    Volatile.Write(ref rateLimitHit, true);
                    return;
                }
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

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;  // 用户取消照常透传
        }
        catch (RateLimitException)
        {
            // v0.6.14.1 旧行为:这里 aggregate 抛 → 丢 partial。改:
            // task 内部不再抛 RateLimitException(改用 RateLimitHit 标志),
            // 这里不再 catch,直接 fall through 返回 partial。
        }
        catch
        {
            // 别的异常(网络/反序列化)同样不丢 partial。
        }

        if (Volatile.Read(ref rateLimitHit))
        {
            var resetHint = "";
            if (resetUnix is not null)
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value).ToLocalTime();
                var waitMin = Math.Max(0, (int)Math.Ceiling((resetAt - DateTimeOffset.Now).TotalMinutes));
                resetHint = $",GitHub 限流将在 {resetAt:HH:mm}(约 {waitMin} 分钟后)重置";
            }
            logger?.Warn("version-rate-limit",
                $"拉取版本时撞 GitHub rate limit,已返回 {result.Count}/{total} 条 partial results" +
                $" (remaining={remaining ?? 0}{resetHint})");
        }

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
    ///
    /// v0.6.14.1 hotfix:撞 rate limit 时**不抛** RateLimitException,
    /// 改回 (empty, RateLimitHeaderInfo { RateLimitHit=true, Reset, Remaining })。
    /// 调用方决定怎么处理:单条调用 GetLatestVersionAsync 收到 hit 会自己
    /// 抛 RateLimitException(单条 fail-fast 没 partial concerns);
    /// 批量调用 FetchVersionsAsync 收到 hit 写共享标志 + return partial。
    /// 这样保证 partial results 不被 Task.WhenAll aggregate exception 吞掉。
    /// </summary>
    private async Task<(List<VersionInfo> Releases, RateLimitHeaderInfo Header)> GetReleasesWithRateLimitAsync(
        string referenceUrl, string? token, CancellationToken ct)
    {
        var (owner, repo) = ParseRepo(referenceUrl);
        if (owner is null || repo is null) return (new List<VersionInfo>(), RateLimitHeaderInfo.Empty);

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
            // v0.6.13-B.2: rate-limit detection(对齐 v0.6.13-B 元数据 service)。
            // v0.6.14.1 改:不再抛 RateLimitException,改回 (empty, hit=true) tuple。
            // 单条调用方自己转 throw,批量调用方不抛只记标志。
            var header = RateLimitHeaderInfo.FromHeaders(resp.Headers);
            if (resp.StatusCode == HttpStatusCode.Forbidden && header.RateLimitRemaining == 0)
            {
                return (new List<VersionInfo>(), header with { RateLimitHit = true });
            }
            if (!resp.IsSuccessStatusCode) return (new List<VersionInfo>(), header);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (new List<VersionInfo>(), header);

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
            return (list, header);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return (new List<VersionInfo>(), RateLimitHeaderInfo.Empty);
        }
    }
}

/// <summary>
/// v0.6.14.1 hotfix:从 GitHub 响应 headers 抓 rate limit 元数据,
/// 让 FetchVersionsAsync 撞 limit 时能给用户"X 分钟后再试"提示。
/// </summary>
public record RateLimitHeaderInfo(
    bool RateLimitHit,
    long? RateLimitRemaining,
    long? RateLimitReset)
{
    public static readonly RateLimitHeaderInfo Empty = new(false, null, null);

    public static RateLimitHeaderInfo FromHeaders(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        long? remaining = null;
        long? reset = null;
        if (headers.TryGetValues("X-RateLimit-Remaining", out var remVals)
            && long.TryParse(remVals.FirstOrDefault(), out var r))
        {
            remaining = r;
        }
        if (headers.TryGetValues("X-RateLimit-Reset", out var rstVals)
            && long.TryParse(rstVals.FirstOrDefault(), out var rs))
        {
            reset = rs;
        }
        return new RateLimitHeaderInfo(false, remaining, reset);
    }
}

public record VersionFetchProgress(int Completed, int Total, string CurrentNodeId);
