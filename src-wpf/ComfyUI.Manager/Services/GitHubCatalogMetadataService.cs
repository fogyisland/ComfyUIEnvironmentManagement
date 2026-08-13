using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.13-B: 调用 GitHub API 给每条 CatalogEntry 填回 11 个 metadata 字段。
/// 策略:round 1 = <c>GET /repos/{o}/{r}</c>,round 2 = 并发 3 endpoint
/// (<c>/readme</c>, <c>/commits/latest</c>, <c>/releases/latest</c>)。
/// Skip non-GitHub reference。Fail-soft,missing fields 留 null。
/// </summary>
public class GitHubCatalogMetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan[] RetryBackoff =
        { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };
    private const int MaxAttempts = 3;
    private static readonly SemaphoreSlim ConcurrencyGate = new(5);

    private readonly HttpClient _http;
    private readonly MetadataCache _cache;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;

    public GitHubCatalogMetadataService(
        HttpClient http, MetadataCache cache, Settings settings, AppLogger? logger = null)
    {
        _http = http;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Enrich each entry's 11 metadata fields + write to cache + write
    /// entry.MetadataFetchedAt. Returns count of entries successfully enriched.
    /// </summary>
    public virtual async Task<int> EnrichAsync(
        IList<CatalogEntry> entries,
        IProgress<MetadataFetchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var done = 0;
        var total = entries.Count;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MetadataFetchProgress(done, total, entry.Package));
            try
            {
                await ConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (await EnrichOneAsync(entry, ct).ConfigureAwait(false))
                    {
                        done++;
                    }
                }
                finally
                {
                    ConcurrencyGate.Release();
                }
            }
            catch (RateLimitException)
            {
                throw;  // 顶层 catch,不继续后面的 entry
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("catalog-metadata", $"enrich fail pkg={entry.Package} reason={ex.Message}");
            }
        }
        progress?.Report(new MetadataFetchProgress(done, total, ""));
        return done;
    }

    private async Task<bool> EnrichOneAsync(CatalogEntry entry, CancellationToken ct)
    {
        var reference = ExtractReference(entry);
        if (string.IsNullOrEmpty(reference) || !reference.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;  // skip non-GitHub
        }
        var (owner, repo) = ParseOwnerRepo(reference);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            return false;
        }
        var repoKey = $"{owner}/{repo}".ToLowerInvariant();

        // 1. Cache hit?
        var cached = await _cache.TryGetAsync(repoKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            ApplyCached(entry, cached);
            entry.MetadataFetchedAt = cached.FetchedAt.ToString("yyyy-MM-ddTHH:mm:ssZ");
            return true;
        }

        // 2. Round 1
        var round1 = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}", ct).ConfigureAwait(false);
        if (round1 is null)
        {
            return false;  // 404 / fail → skip
        }
        using var doc = JsonDocument.Parse(round1);
        var root = doc.RootElement;

        entry.License = TryGetString(root, "license", "spdx_id");
        entry.Stars = TryGetInt(root, "stargazers_count");
        entry.Deprecated = TryGetBool(root, "archived");
        entry.Tags = TryGetStringArray(root, "topics");
        entry.LastCommit = TryGetString(root, "pushed_at");

        // v0.6.14: 7 新字段从 /repos 提取(strict null-check,缺字段 → null/0)
        entry.HtmlUrl = TryGetString(root, "html_url");
        entry.Homepage = TryGetString(root, "homepage");
        entry.Language = TryGetString(root, "language");
        entry.ForksCount = TryGetInt(root, "forks_count");
        entry.OpenIssuesCount = TryGetInt(root, "open_issues_count");
        entry.SubscribersCount = TryGetInt(root, "subscribers_count");
        entry.CreatedAt = TryGetString(root, "created_at");

        // OsCompat MVP: 3 平台全包
        entry.OsCompat = new[] { "windows", "linux", "macos" };
        // PythonCompat MVP: best-effort 空数组
        entry.PythonCompat = Array.Empty<string>();

        // 3. Round 2: 3 concurrent
        var readmeTask = TryGetReadmeAsync(owner, repo, ct);
        var commitsTask = TryGetLatestCommitDateAsync(owner, repo, ct);
        var releasesTask = TryGetLatestReleaseAsync(owner, repo, ct);
        await Task.WhenAll(readmeTask, commitsTask, releasesTask).ConfigureAwait(false);

        if (readmeTask.Result is not null) entry.ReadmeMarkdown = readmeTask.Result;
        if (commitsTask.Result is not null) entry.LastCommit = commitsTask.Result;
        if (releasesTask.Result is not null)
        {
            entry.LatestChangelog = releasesTask.Result.Value.body;
            entry.Downloads = releasesTask.Result.Value.downloads;
            entry.ReleaseTag = releasesTask.Result.Value.tag;  // v0.6.14
        }

        var fetchedAt = DateTime.UtcNow;
        entry.MetadataFetchedAt = fetchedAt.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // 4. 写 cache
        await _cache.SaveAsync(repoKey, new CachedMetadata(
            entry.License, entry.Tags, entry.Stars, entry.Downloads,
            entry.LastCommit, entry.ReadmeMarkdown, entry.LatestChangelog,
            entry.Deprecated, entry.PythonCompat, entry.OsCompat, fetchedAt), ct).ConfigureAwait(false);
        return true;
    }

    private void ApplyCached(CatalogEntry entry, CachedMetadata c)
    {
        entry.License = c.License;
        entry.Tags = c.Tags;
        entry.Stars = c.Stars;
        entry.Downloads = c.Downloads;
        entry.LastCommit = c.LastCommit;
        entry.ReadmeMarkdown = c.ReadmeMarkdown;
        entry.LatestChangelog = c.LatestChangelog;
        entry.Deprecated = c.Deprecated;
        entry.PythonCompat = c.PythonCompat;
        entry.OsCompat = c.OsCompat;
    }

    private async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_settings.GitHubToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.GitHubToken);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                // Rate limit detection
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                        && vals.FirstOrDefault() == "0")
                    {
                        throw new RateLimitException();
                    }
                }
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if ((int)resp.StatusCode >= 500)
                {
                    // 5xx retry
                    if (attempt < MaxAttempts - 1)
                    {
                        await Task.Delay(RetryBackoff[attempt], ct).ConfigureAwait(false);
                        continue;
                    }
                    return null;
                }
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (RateLimitException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (attempt < MaxAttempts - 1)
                {
                    await Task.Delay(RetryBackoff[attempt], ct).ConfigureAwait(false);
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    private async Task<string?> TryGetReadmeAsync(string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/readme", ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var b64 = doc.RootElement.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            // GitHub base64 has \n line breaks; strip them
            b64 = b64.Replace("\n", "").Replace("\r", "");
            var bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    private async Task<string?> TryGetLatestCommitDateAsync(string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/commits/latest", ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("commit").GetProperty("author").GetProperty("date").GetString();
        }
        catch { return null; }
    }

    private async Task<(string body, int downloads, string? tag)?> TryGetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "" : "";
            int downloads = 0;
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("download_count", out var dc) && dc.ValueKind == JsonValueKind.Number)
                    {
                        downloads += dc.GetInt32();
                    }
                }
            }
            // v0.6.14: tag 跟 body / downloads 同 call 拿,零额外 HTTP
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            return (body, downloads, tag);
        }
        catch { return null; }
    }

    private static string? ExtractReference(CatalogEntry entry)
    {
        var rm = entry.RawMetadata;
        if (rm is null) return null;
        foreach (var key in new[] { "reference", "url", "repository" })
        {
            if (rm.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private static (string owner, string repo) ParseOwnerRepo(string reference)
    {
        // github.com/owner/repo[.git][/...]
        try
        {
            var uri = new Uri(reference);
            var segs = uri.AbsolutePath.Trim('/').Split('/');
            if (segs.Length >= 2)
            {
                return (segs[0], segs[1].TrimEnd('.', 'g', 'i', 't'));
            }
        }
        catch { }
        return ("", "");
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        try
        {
            var el = root;
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object) return null;
                if (!el.TryGetProperty(p, out el)) return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch { return null; }
    }

    private static int TryGetInt(JsonElement root, params string[] path)
    {
        try
        {
            var el = root;
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object) return 0;
                if (!el.TryGetProperty(p, out el)) return 0;
            }
            return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
        }
        catch { return 0; }
    }

    private static bool TryGetBool(JsonElement root, params string[] path)
    {
        try
        {
            var el = root;
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object) return false;
                if (!el.TryGetProperty(p, out el)) return false;
            }
            return el.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static IReadOnlyList<string> TryGetStringArray(JsonElement root, params string[] path)
    {
        try
        {
            var el = root;
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
                if (!el.TryGetProperty(p, out el)) return Array.Empty<string>();
            }
            if (el.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }
        catch { return Array.Empty<string>(); }
    }
}

public sealed record MetadataFetchProgress(int Done, int Total, string CurrentPackage);

public sealed class RateLimitException : Exception
{
    public RateLimitException() : base("GitHub API rate limit exceeded") { }
}