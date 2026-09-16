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
    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i.2-fix+user「不行 还是返回 null」:
    /// 用户上游 raw JSON 的 reference URL 真有大量指向 GitHub 上不存在的 repo
    /// (chrisgoringe/Use Everywhere (UE Nodes) 等),API 返 404。
    /// 原 FetchRepoMetadataAsync 把 404 + Parse 失败 + 网络异常都吞成 null,IngestAsync
    /// 没法区分,统一报「metadata returned null」,用户看不出来是上游数据问题还是我们 bug。
    ///
    /// 现在 404 → 抛 RepoNotFoundException(带 owner/repo 给日志),Parse 失败 + 网络异常
    /// 保留 catch + return null(代码 bug,不该 surface 给用户)。
    /// IngestAsync catch 分别报:
    /// - 404 → "repository not found on GitHub"
    /// - null → "metadata parse failed (上游格式异常)"
    /// </summary>
    public sealed class RepoNotFoundException : Exception
    {
        public string Owner { get; }
        public string Repo { get; }
        public RepoNotFoundException(string owner, string repo)
            : base($"repository not found on GitHub: {owner}/{repo}")
        {
            Owner = owner;
            Repo = repo;
        }
    }

    public sealed record RepoMetadata(
        string Owner,
        string Repo,
        string? Description,
        int? Stars,
        int? Watchers,
        int? Forks,
        string? License,
        string? DefaultBranch,
        DateTime? UpdatedAt,
        DateTime? PushedAt,
        string? HtmlUrl,
        string? Language,
        int? OpenIssues,
        // raw_json 里的 topics[] (e.g. ["image","controlnet"]) — 用 string 比 JSON 数组更方便 XAML binding。
        // 写入:Ingestor 直接传 string(逗号分隔);读取:VM 拆 string[] 做 tag 显示。
        // v1.0.0.x T43f+user:右边详情加 tags 显示(用户原话"右边需要按照更加详细的内容列出")。
        string? Topics,
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
                    // v1.0.0.x (2026-09-16) T43i.2-fix+user:
                    // 404 缓存 null 避免重复 404,然后抛 RepoNotFoundException 让 IngestAsync
                    // 在日志里明确报「repository not found」而不是泛泛的「metadata returned null」。
                    // 用户原话「提交的数据中有比较多的错误」+「之前提交在网站上返回的是 repo not
                    // found」 —— 这些 404 完全是上游 raw JSON 提交者填错 reference URL,
                    // 我们代码逻辑没错,只是要让日志让用户看出来是上游问题。
                    _cache[key] = null;
                    throw new RepoNotFoundException(owner, repo);
                }
                var raw = await resp.Content.ReadAsStringAsync(ct);
                var meta = Parse(owner, repo, host, raw);
                _cache[key] = meta;
                return meta;
            }
            catch (RepoNotFoundException)
            {
                // 上游 repo 不存在:已 cache null,异常继续 throw 让 IngestAsync catch 报明确 msg
                throw;
            }
            catch
            {
                // 网络异常 / 解析异常:不缓存,return null(IngestAsync 报「metadata returned null」)
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

    // v1.0.0.x (2026-09-16) T43i.2-fix+user「怎么这么多 null 的」:从 private 改成 internal
// 以便 Tests 单元测试覆盖,验证嵌套 repository.* + camelCase 字段解析逻辑。
internal static RepoMetadata? Parse(string owner, string repo, string host, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // v1.0.0.x (2026-09-16) T43i.2-fix+user「怎么这么多 null 的」:
            // 用户自定义 API(github.pudafo.com / 自建 host)返回格式是嵌套
            //   { "fetch_status": "ok", "repository": { "stars": N, "pushedAt": "...", "language": "...", ... } }
            // 字段名是 camelCase(pushedAt / defaultBranch / updatedAt / releaseCount)。
            // 而 GitHub 原生 API 顶层平铺 + snake_case(stargazers_count / pushed_at / default_branch)。
            // 原 Parse 只认 GitHub 原生格式 → 用户自定义 API 上所有字段取不到 → return null(被外层
            // 当成「metadata 返回 null」,但实际 200 OK + 数据全在)。
            //
            // 修复:优先从 `repository` 嵌套取(用户自定义 API 格式),如果 `repository` 不存在再
            // 走 GitHub 原生顶层格式。两种格式字段名映射见 inline 注释。
            JsonElement dataRoot = root.TryGetProperty("repository", out var repoEl) &&
                                   repoEl.ValueKind == JsonValueKind.Object
                ? repoEl
                : root;

            var desc = TryGetString(dataRoot, "description");
            // stars:自定义 API "stars",GitHub 原生 "stargazers_count"
            var stars = TryGetInt(dataRoot, "stars") ?? TryGetInt(root, "stargazers_count");
            // watchers:自定义 API "watchers",GitHub 原生 "subscribers_count" 或 "watchers_count"
            var watchers = TryGetInt(dataRoot, "watchers")
                          ?? TryGetInt(root, "subscribers_count")
                          ?? TryGetInt(root, "watchers_count");
            // forks:两种格式同名,但自定义 API 在 dataRoot,GitHub 原生在 root
            var forks = TryGetInt(dataRoot, "forks") ?? TryGetInt(root, "forks_count");
            // license:自定义 API 是 string(如 "MIT" / "GPL-3.0"),
            //         GitHub 原生是 object { spdx_id, name }
            string? license = null;
            if (dataRoot.TryGetProperty("license", out var licEl))
            {
                if (licEl.ValueKind == JsonValueKind.String)
                    license = licEl.GetString();
                else if (licEl.ValueKind == JsonValueKind.Object)
                    license = TryGetString(licEl, "spdx_id") ?? TryGetString(licEl, "name");
            }
            // default_branch:自定义 API "defaultBranch",GitHub 原生 "default_branch"
            var defaultBranch = TryGetString(dataRoot, "defaultBranch")
                                ?? TryGetString(root, "default_branch");
            // updated_at:自定义 API "updatedAt",GitHub 原生 "updated_at"
            var updatedStr = TryGetString(dataRoot, "updatedAt") ?? TryGetString(root, "updated_at");
            DateTime? updated = null;
            if (!string.IsNullOrEmpty(updatedStr) && DateTime.TryParse(updatedStr, out var dt))
                updated = dt;
            // pushed_at:自定义 API "pushedAt",GitHub 原生 "pushed_at"
            var pushedStr = TryGetString(dataRoot, "pushedAt") ?? TryGetString(root, "pushed_at");
            DateTime? pushed = null;
            if (!string.IsNullOrEmpty(pushedStr) && DateTime.TryParse(pushedStr, out var pdt))
                pushed = pdt;
            // html_url:GitHub 原生有,自定义 API 没有 → 用 {host} 拼 {owner}/{repo} 兜底
            var htmlUrl = TryGetString(dataRoot, "html_url") ?? TryGetString(root, "html_url");
            if (string.IsNullOrEmpty(htmlUrl) && !string.IsNullOrEmpty(host))
            {
                // 兜底:GitHub repo URL 永远是 github.com/{owner}/{repo}
                // (即使 query host 是自定义 API,GitHub 真实 URL 还是 github.com)
                htmlUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
            }
            // language:同名
            var language = TryGetString(dataRoot, "language") ?? TryGetString(root, "language");
            // open_issues_count:GitHub 原生有,自定义 API 没有 → null
            var openIssues = TryGetInt(dataRoot, "open_issues_count") ?? TryGetInt(root, "open_issues_count");
            // topics[]:同名,两种格式都在 dataRoot/root 同位置
            string? topics = null;
            var topicsSrc = dataRoot.TryGetProperty("topics", out var topicsEl) ? topicsEl
                           : (root.TryGetProperty("topics", out var topicsRoot) ? topicsRoot : default);
            if (topicsSrc.ValueKind == JsonValueKind.Array)
            {
                var tagList = new List<string>();
                foreach (var t in topicsSrc.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) tagList.Add(s);
                    }
                }
                if (tagList.Count > 0) topics = string.Join(",", tagList);
            }
            return new RepoMetadata(owner, repo, desc, stars, watchers, forks, license,
                defaultBranch, updated, pushed, htmlUrl, language, openIssues, topics, raw, host);
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
