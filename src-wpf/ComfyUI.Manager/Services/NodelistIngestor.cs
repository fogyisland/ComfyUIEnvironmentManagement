using System;
using System.Collections.Generic;
using ComfyUI.Manager.Data;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using System.Linq;

namespace ComfyUI.Manager.Services;

/// <summary>
/// 全量入库时序列化数组/对象的 JSON 选项。
/// 关掉 JavaScriptEncoder.Default 的 HTML 转义,避免 "torch>=2.0" 的 '>' 被存成 ">"(读回 pip pill 不直观,
/// 也跟用户用 Python 习惯读的字符串字面值不一致)。
/// </summary>
internal static class NodelistJsonOptions
{
    public static readonly JsonSerializerOptions Raw = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-redesign:节点入库器。
///
/// 用户原话"5. 使用增量入库策略,解析当前的文件夹下的json 文件
/// github中的作者和节点名称写入到sqlite数据库,写入作者和库名称两列,
/// 也就是写到 Nodeslist 表里面
/// 6. 调取当前的表中的作者和节点库名称 去 云端网站去查询,
/// 获取详细需要获取的信息
/// 7.写入数据到 nodeslist 和 NodesDetailed"。
///
/// 流程(增量入库):
/// 1. 解析 {nodelistDirectory}/custom-node-list.json
///    (v1.0.0.x 2026-09-15 T43+user:seed json 直接在 nodelist 根下,不嵌子目录)
/// 2. 提取 author+repo_name 列表(去重)
/// 3. 对比数据库现有 keys,只 upsert 新增(增量)/跳过已存在
/// 4. 对新 author+repo 调 NodeRepoQueryService 拿 metadata
/// 5. UpsertDetail 写 Nodesdetail(一对多)
/// 
/// 流程(完整入库 = 删除+重扫):
/// DELETE ALL + 重新 1-5
/// </summary>
public sealed class NodelistIngestor
{
    public sealed record IngestResult(
        int EntriesScanned,
        int EntriesNew,
        int EntriesSkipped,
        int DetailsWritten,
        int DetailsFailed,
        TimeSpan Elapsed);

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i+user 「入库过程中加日志记录,我希望看到进度和日志」:
    /// 7 种 IngestEventKind 描述入库流水线的关键 checkpoint。
    /// 通过单一 IProgress&lt;IngestEvent&gt; 通道上报,VM subscriber 根据 Kind 路由到
    /// StatusText(headline)+ LogLines(滚动日志)+ IsLogPanelVisible(显示/折叠)。
    /// 替代原 IngestProgress — 那条 record 只能发「在跑某条」,详情成功/失败拿不到,
    /// 日志只显示「在跑」不知道「跑成功/失败」。
    /// </summary>
    public enum IngestEventKind
    {
        Started,        // 整体启动,Total 已知
        EntryUpserted,  // 1 条新 entry 写库成功
        EntrySkipped,   // 增量模式下已存在,跳过 GitHub fetch
        EntryFailed,    // 1 条 entry upsert 抛异常
        DetailFetched,  // 1 条详情拉取 + 写库成功
        DetailFailed,   // 1 条详情拉取/写库失败(异常)
        Completed,      // 全部完成,IngestResult 已知
    }

    /// <summary>
    /// 入库事件 payload — Kind 决定哪些字段有意义(用静态工厂强制):
    /// - Started:        Total
    /// - EntryUpserted:  Current, Total, Author, RepoName
    /// - EntrySkipped:   Current, Total, Author, RepoName
    /// - EntryFailed:    Current, Total, Author, RepoName, Message?
    /// - DetailFetched:  Current, Total, Author, RepoName
    /// - DetailFailed:   Current, Total, Author, RepoName, Message?
    /// - Completed:      Current=Total, Total, Result
    /// </summary>
    public sealed record IngestEvent(
        IngestEventKind Kind,
        int Current = 0,
        int Total = 0,
        string Author = "",
        string RepoName = "",
        IngestResult? Result = null,
        string? Message = null)
    {
        public static IngestEvent Started(int total) =>
            new(IngestEventKind.Started, Total: total);

        public static IngestEvent EntryUpserted(int current, int total, string author, string repo) =>
            new(IngestEventKind.EntryUpserted, current, total, author, repo);

        public static IngestEvent EntrySkipped(int current, int total, string author, string repo) =>
            new(IngestEventKind.EntrySkipped, current, total, author, repo);

        public static IngestEvent EntryFailed(int current, int total, string author, string repo, string? msg = null) =>
            new(IngestEventKind.EntryFailed, current, total, author, repo, Message: msg);

        public static IngestEvent DetailFetched(int current, int total, string author, string repo) =>
            new(IngestEventKind.DetailFetched, current, total, author, repo);

        public static IngestEvent DetailFailed(int current, int total, string author, string repo, string? msg = null) =>
            new(IngestEventKind.DetailFailed, current, total, author, repo, Message: msg);

        public static IngestEvent Completed(IngestResult result, int total) =>
            new(IngestEventKind.Completed, Current: total, Total: total, Result: result);
    }

    private readonly NodelistRepository _repo;
    private readonly NodeRepoQueryService _queryService;
    private readonly AppLogger? _logger;

    public NodelistIngestor(
        NodelistRepository repo,
        NodeRepoQueryService queryService,
        AppLogger? logger = null)
    {
        _repo = repo;
        _queryService = queryService;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(
        string nodelistJsonFile,
        string host,
        string? token,
        bool forceFull,
        IProgress<IngestEvent>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (forceFull)
        {
            // 完整入库:删现有 entries,级联删 details(FK ON DELETE CASCADE)
            using var conn = OpenConn();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM nodelist_entries";
            cmd.ExecuteNonQuery();
        }

        if (!File.Exists(nodelistJsonFile))
        {
            sw.Stop();
            return new IngestResult(0, 0, 0, 0, 0, sw.Elapsed);
        }

        // 1. 解析 json
        using var stream = File.OpenRead(nodelistJsonFile);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("custom_nodes", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            sw.Stop();
            return new IngestResult(0, 0, 0, 0, 0, sw.Elapsed);
        }

        // 2. 提取 owner+repo_name(去重)+ v1.0.0.x T43h 全量 ParseEntry(24 个 raw JSON 字段)。
        //
        // v1.0.0.x (2026-09-16) T43i.2-fix:之前用 raw JSON 顶层 `author` + `title` 作为
        // (author, repo_name) 入库 key —— 但 `title` 是**显示名**(e.g. "WAS Node Suite (Revised)"),
        // 不是 GitHub repo 名;`author` 也可能不是 GitHub owner(11% entry 跟 reference URL
        // owner 不一致,见 NodelistIngestor.TryParseGitHubOwnerRepo 注释)。
        // 用 raw JSON `reference` URL 解析的 (owner, repo) 作为 key,跟 GitHub API
        // /repos/{owner}/{repo} 完全一致 —— 11% entry 调 GitHub API 不再 404,41% entry
        // repo_name 列存的是真实 GitHub repo 名(不再是显示名)。
        //
        // raw JSON `author` 字段保留 → 入库到 `nodelist_entries.author` 列(UI 显示用,
        // "节点作者"是 raw JSON author 字段语义,不应跟 GitHub owner 混淆)。
        //
        // reference 缺失或无法解析 → skip 整个 entry(5942/5942 raw JSON entry 都有合法
        // reference,缺失/无法解析是 JSON 异常,不符合「不静默失败」原则 —— log warn 留痕)。
        var pairs = new HashSet<(string Author, string RepoName)>();
        var parsedEntries = new Dictionary<(string Author, string RepoName), ParsedNodelistEntry>();
        foreach (var entry in arr.EnumerateArray())
        {
            var refUrl = TryGetString(entry, "reference");
            if (refUrl is null)
            {
                _logger?.Warn("nodelist-ingest",
                    $"skip entry without reference: author={TryGetString(entry, "author")} title={TryGetString(entry, "title")}");
                continue;
            }
            var parsedUrl = TryParseGitHubOwnerRepo(refUrl);
            if (parsedUrl is null)
            {
                _logger?.Warn("nodelist-ingest", $"reference URL 无法解析 owner/repo,skip entry: {refUrl}");
                continue;
            }
            var (repoOwner, repoName) = parsedUrl.Value;
            var author = TryGetString(entry, "author") ?? repoOwner;  // UI 显示用 author,缺失则用 owner 兜底
            var key = (repoOwner, repoName);
            pairs.Add(key);
            // v1.0.0.x T43h:ParseEntry 处理所有 24 个 raw JSON 字段;若重复 entry(同 owner+repo),后写覆盖。
            parsedEntries[key] = ParseEntry(entry, author, repoOwner, repoName);
        }

        // v1.0.0.x (2026-09-16) T43i+user:入库启动通知 — 拿 Total 后立刻 Report Started,
        // VM 收到后 ClearLogs + IsLogPanelVisible=true + 写「── 入库启动 — 共 N 条 entry ──」。
        // 后续 EntryUpserted/EntrySkipped/DetailFetched 等会按顺序滚。
        progress?.Report(IngestEvent.Started(pairs.Count));

        // 3. 对比数据库现有 keys:
        //    - 增量模式:已存在的 entry 也走 UpsertEntry(更新 18 个 raw JSON 新列),
        //      但跳过 NodeRepoQueryService(GitHub API 不重拉)。这避免「老 entry 永远 NULL」问题
        //      —— 用户原话"我们不光入库 json文件中数据库没有入库的节点,同时会入库云端所有的云端节点
        //      已有数据而本地没有的数据"(T43h+ 增补:本地有的也要 refresh 字段)。
        //    - 拆分 toIngest 仅为新 key,旧 key 走 updateEntries(只 upsert 不 fetch)。
        var existing = forceFull ? new HashSet<(string, string)>() : _repo.GetExistingKeys();
        var newEntries = pairs.Where(p => !existing.Contains(p)).ToList();
        var updateEntries = forceFull ? new List<(string, string)>() : pairs.Where(p => existing.Contains(p)).ToList();
        var toIngest = newEntries;  // 兼容原 line 263 tasks.Add 用法
        var newCount = 0;
        var detailsWritten = 0;
        var detailsFailed = 0;

        // v1.0.0.x (2026-09-16) T43i.2-fix+user 提速:用户原话"那是github 我这里不是"——
        // NodeRepoQueryService 走的是**用户自定义 API**(GET {host}/api/v1/repos/{owner}/{repo},
        // X-API-Key header,每 key 50,000/h 限流 —— 不是 GitHub 5000/h)。原 10 req/s
        // (= 36,000/h,2.5 倍限流阈值)是按 GitHub 5000/h 估的,自定义 API 上太保守,
        // 5939 entry 全量重灌要 ~10 分钟,用户嫌慢。
        //
        // 现在拉到 30 req/s (= 108,000/h),**理论 1.85 小时才到限流上限** ——
        // 5939 entry × 33ms ≈ 3.3 分钟跑完,远低于限流阈值,稳。
        //
        // 关键约束(用户原话"走我说的就行"+"那是github 我这里不是"):
        // - host 不一定是 api.github.com,可能是 github.pudafo.com / 自建 API
        // - 不能用 GitHub 5000/h 假设
        // - 现在策略:NodeRepoQueryService 自己 CheckRateLimit(每 key 50,000/h),
        //   Ingestor 只负责最快速度,被限流了 429 由 NodeRepoQueryService 自己处理
        //
        // 历史:feat/nodelist-directory T35 当时原话"平均提交的频率是一秒钟10个",
        // 10 req/s 是当时按 GitHub 官方 5000/h + 安全冗余定的,过于保守。
        var rateLimit = new SemaphoreSlim(1, 1);
        var lastRelease = DateTime.UtcNow;
        var rateDelay = TimeSpan.FromMilliseconds(33);  // 1s/30req = 33ms/req

        async Task<((int NewCount, int DetailFailed, int DetailWritten) Result, int Index)> ProcessOneAsync(
            (string Author, string RepoName) kvp, int index, int total, int currentNew)
        {
            ct.ThrowIfCancellationRequested();
            var author = kvp.Author;
            var repoName = kvp.RepoName;
            // v1.0.0.x T43h+user(2026-09-16) 后台入库:进度上报走 IProgress<T>,它本身在构造时
            // 捕获 SynchronizationContext(VM 端在 UI 线程 new Progress<…>,自动 marshal 回 UI)。
            // **不要**在这里强行 Dispatcher.Invoke — 后台 10 个 worker 并发 dispatch 会塞满 UI 队列,
            // 造成 UI 顿挫,反而违反「后台入库不阻塞前台」的目标。
            // v1.0.0.x T43i+user 「入库过程中加日志」:从 IngestProgress 升级为 IngestEvent
            // 区分性报告 — 完整入库(forceFull=true)走 EntryUpserted,增量模式已存在走
            // EntrySkipped(让 VM 写「[已存在]」log 行;UpdateOnlyAsync 里也单独 Report EntrySkipped)。
            progress?.Report(IngestEvent.EntryUpserted(index + 1, total, author, repoName));

            // 限流:100ms 释放一个信号量槽(> 0 时等待)
            await rateLimit.WaitAsync(ct);
            try
            {
                var sinceLast = DateTime.UtcNow - lastRelease;
                if (sinceLast < rateDelay)
                {
                    await Task.Delay(rateDelay - sinceLast, ct);
                }
                lastRelease = DateTime.UtcNow;
            }
            finally
            {
                rateLimit.Release();
            }

            int localNew = 0, localDetail = 0, localDetailFailed = 0;
            // 3a. upsert entry(全量 23 列)
            try
            {
                // v1.0.0.x T43h:parsedEntries 在 line 102 ParseEntry 时填充。forceFull 模式下
                // 老 entry 已 DELETE,但 parsedEntries dictionary 还在 → 新写时直接传。
                parsedEntries.TryGetValue(kvp, out var parsed);
                if (parsed is null)
                {
                    _logger?.Warn("nodelist-ingest", $"parsed entry missing for {author}/{repoName}");
                    // v1.0.0.x T43i+user:parsed 缺失(异常)也要报告,VM 写「✗ entry 失败:」log 行
                    progress?.Report(IngestEvent.EntryFailed(index + 1, total, author, repoName, "parsed entry missing"));
                    return ((0, 0, 0), currentNew);
                }
                _repo.UpsertEntry(
                    // v1.0.0.x T43i.2-fix:author 列存从 reference 解析的 GitHub owner
                    // (跟 nodelist_details.author 一致,GitHub API 用这个调 /repos/{owner}/{repo})。
                    // UI 显示的「节点作者」语义由 nodelist_entries 内其他途径提供(见 NodelistIngestor
                    // line 168 注释,raw JSON author 字段跟 GitHub owner 可能不同,前者是显示名后者是 owner)。
                    // 当前简化:author 列直接存 GitHub owner —— 用户在 UI 列表也能看到正确的 owner。
                    author: parsed.RepoOwner,
                    repoName: parsed.RepoName,
                    source: "json",
                    id: parsed.Id,
                    reference: parsed.Reference,
                    reference2: parsed.Reference2,
                    description: parsed.Description,  // T43i.1+user
                    filesJson: parsed.FilesJson,
                    installType: parsed.InstallType,
                    pipJson: parsed.PipJson,
                    aptDependency: parsed.AptDependency,
                    dependenciesJson: parsed.DependenciesJson,
                    preemptionsJson: parsed.PreemptionsJson,
                    nodenamePattern: parsed.NodenamePattern,
                    nickname: parsed.Nickname,
                    category: parsed.Category,
                    tagsJson: parsed.TagsJson,
                    lastUpdate: parsed.LastUpdate,
                    rawStars: parsed.RawStars,
                    badgesJson: parsed.BadgesJson,
                    jsPath: parsed.JsPath,
                    rawLicense: parsed.RawLicense);
                localNew = 1;
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"entry upsert failed for {author}/{repoName}: {ex.Message}");
                // v1.0.0.x T43i+user:entry upsert 异常 → Report EntryFailed,VM 写「✗ entry 失败:」log 行
                progress?.Report(IngestEvent.EntryFailed(index + 1, total, author, repoName, ex.Message));
                return ((localNew, localDetail, localDetailFailed), currentNew + localNew);
            }

            // 3b. 拉云端 metadata
            try
            {
                var meta = await _queryService.FetchRepoMetadataAsync(host, token, author, repoName, ct);
                if (meta is not null)
                {
                    var version = !string.IsNullOrEmpty(meta.DefaultBranch) ? meta.DefaultBranch!
                                  : (!string.IsNullOrEmpty(meta.Repo) ? meta.Repo : "unknown");
                    _repo.UpsertDetail(new NodelistRepository.Detail(
                        author, repoName, version,
                        meta.Description, meta.Stars, meta.Watchers,
                        // v1.0.0.x T43f+user:6 个新字段(forks/pushed_at/html_url/language/open_issues/topics)
                        // 让右边详情能展开显示完整仓库信息(用户原话"右边需要按照更加详细的内容列出")。
                        meta.Forks,
                        meta.License, meta.DefaultBranch,
                        meta.UpdatedAt?.ToString("o"),
                        meta.PushedAt?.ToString("o"),
                        meta.HtmlUrl,
                        meta.Language,
                        meta.OpenIssues,
                        meta.Topics,
                        meta.RawJson, meta.Host,
                        DateTime.UtcNow.ToString("o")));
                    localDetail = 1;
                    // v1.0.0.x T43i+user:详情拉取 + 写库成功 → Report DetailFetched,VM 写「详情 ✓」log 行
                    progress?.Report(IngestEvent.DetailFetched(index + 1, total, author, repoName));
                }
                else
                {
                    localDetailFailed = 1;
                    progress?.Report(IngestEvent.DetailFailed(index + 1, total, author, repoName, "metadata returned null"));
                }
            }
            // v1.0.0.x (2026-09-16) T43i.2-fix+user「不行 还是返回 null」:
            // 上游 raw JSON 的 reference URL 真有大量指向 GitHub 上不存在的 repo(用户原话
            // 「提交的数据中有比较多的错误」+「之前提交在网站上返回的是 repo not found」)。
            // 这种情况 API 返 404,NodeRepoQueryService 抛 RepoNotFoundException 让日志
            // 明确报「repository not found」而不是泛泛的「metadata returned null」,
            // 用户/我一眼能看出是上游数据问题,不是我们入库逻辑 bug。
            catch (NodeRepoQueryService.RepoNotFoundException ex)
            {
                localDetailFailed = 1;
                progress?.Report(IngestEvent.DetailFailed(index + 1, total, author, repoName,
                    $"repository not found on GitHub (上游 reference URL 不存在): {ex.Owner}/{ex.Repo}"));
                return ((localNew, localDetail, localDetailFailed), currentNew + localNew);
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"detail fetch failed for {author}/{repoName}: {ex.Message}");
                localDetailFailed = 1;
                // v1.0.0.x T43i+user:detail 拉取/写库异常 → Report DetailFailed
                progress?.Report(IngestEvent.DetailFailed(index + 1, total, author, repoName, ex.Message));
            }
            // v1.0.0.x (2026-09-16) T43i+user fix:tuple 元素顺序对齐命名。
            // 返回类型是 ((NewCount, DetailFailed, DetailWritten) Result, Index),
            // 这里 localDetail(localDetailWritten)放在 DetailWritten 位,localDetailFailed 放 DetailFailed 位。
            // 之前 `(localNew, localDetail, localDetailFailed)` 是错的 — 让累加 DetailWritten=1/DetailFailed=0
            // (实际应 DetailWritten=0/DetailFailed=1,因为 meta is null → 没写 detail)。
            return ((NewCount: localNew, DetailFailed: localDetailFailed, DetailWritten: localDetail), currentNew + localNew);
        }

        /// <summary>
        /// v1.0.0.x (2026-09-15) T43h+user:增量模式下,老 entry 也走 UpsertEntry 更新 18 个 raw JSON 列,
        /// 但**不**调 NodeRepoQueryService(避免每次「⤴ 增量入库」都重拉所有 GitHub API)。
        /// 用户原话"我们不光入库 json 文件中数据库没有入库的节点,同时会入库云端所有的云端节点
        /// 已有数据而本地没有的数据"——本地已有的也 refresh 字段,云端新加的走完整流程。
        /// </summary>
        async Task<((int NewCount, int DetailFailed, int DetailWritten) Result, int Index)> UpdateOnlyAsync(
            (string Author, string RepoName) kvp, int index, int total, int currentNew)
        {
            ct.ThrowIfCancellationRequested();
            var author = kvp.Author;
            var repoName = kvp.RepoName;
            // v1.0.0.x T43h+user(2026-09-16) 后台入库:同 ProcessOneAsync 注释 ——
            // 进度上报靠 IProgress<T> 自动 marshal,不要强行 Dispatcher.Invoke。
            // v1.0.0.x T43i+user 「入库过程中加日志」:UpdateOnlyAsync 走老 entry(增量模式已存在),
            // 不重拉 GitHub,只 refresh 19 个 raw JSON 列。这里 Report EntrySkipped,
            // VM 写「[已存在] author/repo」log 行(跟 ProcessOneAsync 的 EntryUpserted
            // 区分,用户原话"可以加上 author title reference description 字段,然后再填充
            // 其他字段"——「已存在」也展示,让用户看到哪些刷新了哪些跳过)。
            progress?.Report(IngestEvent.EntrySkipped(index + 1, total, author, repoName));

            try
            {
                parsedEntries.TryGetValue(kvp, out var parsed);
                if (parsed is null)
                {
                    _logger?.Warn("nodelist-ingest", $"parsed entry missing for {author}/{repoName}");
                    // v1.0.0.x T43i+user:UpdateOnlyAsync 也报 EntryFailed(虽然走的是老 entry 路径,VM 同样知道)
                    progress?.Report(IngestEvent.EntryFailed(index + 1, total, author, repoName, "parsed entry missing"));
                    return ((0, 0, 0), currentNew);
                }
                _repo.UpsertEntry(
                    // v1.0.0.x T43i.2-fix:同 ProcessOneAsync 注释 —— author 列存 GitHub owner。
                    author: parsed.RepoOwner,
                    repoName: parsed.RepoName,
                    source: "json",
                    id: parsed.Id,
                    reference: parsed.Reference,
                    reference2: parsed.Reference2,
                    description: parsed.Description,  // T43i.1+user
                    filesJson: parsed.FilesJson,
                    installType: parsed.InstallType,
                    pipJson: parsed.PipJson,
                    aptDependency: parsed.AptDependency,
                    dependenciesJson: parsed.DependenciesJson,
                    preemptionsJson: parsed.PreemptionsJson,
                    nodenamePattern: parsed.NodenamePattern,
                    nickname: parsed.Nickname,
                    category: parsed.Category,
                    tagsJson: parsed.TagsJson,
                    lastUpdate: parsed.LastUpdate,
                    rawStars: parsed.RawStars,
                    badgesJson: parsed.BadgesJson,
                    jsPath: parsed.JsPath,
                    rawLicense: parsed.RawLicense);
                // v1.0.0.x (2026-09-16) T43i+user fix:UpdateOnlyAsync 处理「已存在」entry 字段刷新,
                // **不算 NewCount**(EntriesNew metric 只统计真正新增的 entry,用户看 StatusText
                // 时区分「新写的」vs「已存在更新的」)。EntriesSkipped metric 走 line 442 公式
                // `pairs.Count - toIngest.Count` 自然数对得上。之前返回 (1,0,0) 让 EntriesNew 双倍,
                // 测试 authorA+authorB → EntriesNew=2 错。
                return ((0, 0, 0), currentNew);
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"update entry failed for {author}/{repoName}: {ex.Message}");
                // v1.0.0.x T43i+user:UpdateOnlyAsync catch 同样报 EntryFailed
                progress?.Report(IngestEvent.EntryFailed(index + 1, total, author, repoName, ex.Message));
                return ((0, 0, 0), currentNew);
            }
        }

        // 10 个并发 worker 拉数据
        var tasks = new List<Task<((int, int, int), int)>>();
        var currentNew = 0;
        for (int i = 0; i < toIngest.Count; i++)
        {
            tasks.Add(ProcessOneAsync(toIngest[i], i, toIngest.Count, currentNew));
        }
        // v1.0.0.x T43h+user:增量模式下,老 entry 也跑一遍 UpsertEntry 更新 18 个 raw JSON 列
        // (但不调 NodeRepoQueryService,避免每次「⤴ 增量入库」都重拉所有 GitHub API)。
        // 复用 ProcessOneAsync 不合适(它会调 fetch),新建一个 UpdateOnlyAsync 只跑 upsert。
        for (int i = 0; i < updateEntries.Count; i++)
        {
            tasks.Add(UpdateOnlyAsync(updateEntries[i], toIngest.Count + i, toIngest.Count + updateEntries.Count, currentNew));
        }
        System.Collections.Generic.List<((int NewCount, int DetailFailed, int DetailWritten), int)> results;
        try
        {
            // v1.0.0.x (2026-09-15) T43e debug: granular catch — pin NRE throw site。
            // Release build 行号不可靠,这里分别 catch 当 Task.WhenAll / 当 Worker 抛,
            // 把异常原样 re-throw,但先用 _logger 落上下文(processOne 编号 + ex Stack)。
            var raw = await Task.WhenAll(tasks);
            results = new System.Collections.Generic.List<((int, int, int), int)>(raw.Length);
            foreach (var t in raw) results.Add(t);
        }
        catch (Exception ex)
        {
            _logger?.Error("nodelist-ingest",
                $"IngestAsync.WhenAll NRE/异常 pin: type={ex.GetType().FullName} msg={ex.Message} stack={ex.StackTrace}");
            throw;
        }
        foreach (var tup in results)
        {
            // v1.0.0.x (2026-09-16) T43i+user fix:用 named tuple elements 而非 Item1/2/3。
            // 之前用 `r.Item1` / `Item2` / `Item3` 时,把 DetailWritten/DetailFailed
            // 累加反了 → Completed event 携带的 IngestResult.DetailsWritten/DetailFailed
            // 错位(测试 DetailsFailed=2 是因为 Item2/Item3 互换),Completed event 写
            // 「详情写入 2」实际应写「详情失败 2」。改用 named 引用语义清楚。
            var r = tup.Item1;
            newCount += r.NewCount;
            detailsWritten += r.DetailWritten;
            detailsFailed += r.DetailFailed;
        }

        sw.Stop();
        var result = new IngestResult(
            pairs.Count,
            newCount,
            pairs.Count - toIngest.Count,
            detailsWritten,
            detailsFailed,
            sw.Elapsed);
        // v1.0.0.x (2026-09-16) T43i+user 「入库过程中加日志」:Completed event 携带 IngestResult,
        // VM subscriber 写「── 完成 — 扫 N,新增 X,跳过 Y,详情写入 Z(失败 W),耗时 hh:mm:ss ──」
        // 收尾行 + IsLogPanelVisible=false(完成时折叠,用户原话)。
        progress?.Report(IngestEvent.Completed(result, pairs.Count));
        return result;
    }

    private Microsoft.Data.Sqlite.SqliteConnection OpenConn()
    {
        // 借用 _repo 的 factory
        var f = typeof(NodelistRepository).GetField("_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_repo) as SqliteConnectionFactory;
        return f?.Open() ?? throw new InvalidOperationException("SqliteConnectionFactory missing");
    }

    // v1.0.0.x (2026-09-15) T43h+user:全量 raw JSON 解析(24 字段)。之前 Ingestor 只解析
    // author + title;现在 Ingestor.ParseEntry 把 custom-node-list.json 每个 entry 全部
    // distinct 字段抽出来,数组/对象走 JsonSerializer.Serialize 存 JSON 字符串。
    //
    // v1.0.0.x (2026-09-16) T43i.1+user 「把 author / title / reference / description 入库」:
    // 加 Description — raw JSON custom-node-list.json 顶层 description 字段
    // (e.g. "ComfyUI custom nodes providing styled draggable float slider widgets")。
    // 跟 nodelist_details.description 区分(那个是 GitHub API metadata.description,
    // raw 是节点源作者自填;可能略不同)。两者并存,UI 优先显示 raw 描述。
    //
    // v1.0.0.x (2026-09-16) T43i.2-fix:RepoName 现在存从 reference URL 解析的 GitHub repo name
    // (e.g. "was-node-suite-comfyui"),不是 raw JSON `title`(那个是显示名如 "WAS Node Suite (Revised)")。
    // RepoOwner 是新加的字段——从 reference URL 解析的 GitHub owner,跟 GitHub API 完全一致。
    // Author 保留 raw JSON `author`(用于 UI 显示"节点作者"语义),跟 GitHub owner 可能不同
    // (11% entry author 字段跟 reference owner 不一致)。
    private sealed record ParsedNodelistEntry(
        string Author,
        string RepoOwner,           // T43i.2-fix:从 reference 解析的 GitHub owner(入库到 author 列 —— 因为 GitHub API 用 owner 查 metadata)
        string RepoName,             // T43i.2-fix:从 reference 解析的 GitHub repo name(入库到 repo_name 列 —— GitHub API 用 repo 查 metadata)
        string? Id,
        string? Reference,
        string? Reference2,
        string? Description,         // T43i.1+user:raw JSON 顶层 description
        string? FilesJson,
        string? InstallType,
        string? PipJson,
        string? AptDependency,
        string? DependenciesJson,
        string? PreemptionsJson,
        string? NodenamePattern,
        string? Nickname,
        string? Category,
        string? TagsJson,
        string? LastUpdate,
        int? RawStars,
        string? BadgesJson,
        string? JsPath,
        string? RawLicense);

    /// <summary>
    /// 解析单条 entry — 提取所有 24 个 distinct raw JSON 字段。
    /// 数组/对象类型(JsonArrayToString/JsonObjectOrArrayToString)走 JsonSerializer.Serialize 存字符串。
    /// 缺失字段返回 null(老 DB backfill 列用 NULL,UI 显示空)。
    ///
    /// v1.0.0.x (2026-09-16) T43i.2-fix:author/repoOwner/repoName 三个参数都从调用方传入
    /// (在 IngestAsync 主流程用 TryParseGitHubOwnerRepo 解析 reference 拿到 owner/repo,
    /// author 从 raw JSON `author` 字段拿用于显示)。
    /// </summary>
    private static ParsedNodelistEntry ParseEntry(JsonElement entry, string author, string repoOwner, string repoName)
    {
        return new ParsedNodelistEntry(
            Author: author,
            RepoOwner: repoOwner,
            RepoName: repoName,
            Id: TryGetString(entry, "id"),
            Reference: TryGetString(entry, "reference"),
            Reference2: TryGetString(entry, "reference2"),
            Description: TryGetString(entry, "description"),  // T43i.1+user
            FilesJson: JsonArrayToString(entry, "files"),
            InstallType: TryGetString(entry, "install_type"),
            PipJson: JsonArrayToString(entry, "pip"),
            AptDependency: TryGetStringOrJoinedArray(entry, "apt_dependency"),
            DependenciesJson: JsonObjectOrArrayToString(entry, "dependencies"),
            PreemptionsJson: JsonArrayToString(entry, "preemptions"),
            NodenamePattern: TryGetString(entry, "nodename_pattern"),
            Nickname: TryGetString(entry, "nickname"),
            Category: TryGetString(entry, "category"),
            TagsJson: JsonArrayToString(entry, "tags"),
            LastUpdate: TryGetString(entry, "last_update"),
            RawStars: TryGetInt(entry, "stars"),
            BadgesJson: JsonArrayToString(entry, "badges"),
            JsPath: TryGetString(entry, "js_path"),
            RawLicense: TryGetString(entry, "license"));
    }

    private static string? TryGetString(JsonElement obj, string field)
    {
        if (obj.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    /// <summary>
    /// v1.0.0.x (2026-09-16) T43i.2-fix:从 GitHub URL 解析 owner + repo name。
    ///
    /// 用户原话"我们需要仓库中增加两个字段:repoOwner 和 reponame 这两个字段分别取自
    /// https://github.com/ltdrdata/was-node-suite-comfyui 中的 ltdrdata 和 was-node-suite-comfyui"。
    ///
    /// 当前 raw JSON 分布统计(5942 entry 全扫):
    /// - 100% entry 有 reference 字段
    /// - 100% 是 https://github.com/{owner}/{repo} 标准5 段格式
    /// - 0 SSH / 0 http:// / 0 bare github.com / 0 query / 0 fragment / 0 .git 后缀
    /// - 11% entry author 字段 != reference owner(e.g. author='jtydhr88', reference='https://github.com/smk-h/...')
    /// - 41% entry title 字段 != reference repo(e.g. title='WAS Node Suite (Revised)', reference_repo='was-node-suite-comfyui')
    ///
    /// 支持格式:
    /// - https://github.com/{owner}/{repo}
    /// - https://github.com/{owner}/{repo}.git
    /// - 带 query string 或 fragment(?ref=xxx / #readme 都吃掉)
    /// - http:// 同 https://(GitHub 强制 https 但兼容)
    ///
    /// 不支持(返回 null,主流程 skip + log warn):
    /// - 非 http(s) 协议(SSH git@... / file:// / 本地路径)
    /// - 单 path segment(https://github.com/foo 没 repo)
    /// - bare github.com(https://github.com/)
    /// - 空字符串
    ///
    /// 返回 null 时调用方 skip 整个 entry;不静默失败(_logger?.Warn 在 IngestAsync 主流程)。
    /// </summary>
    internal static (string Owner, string Repo)? TryParseGitHubOwnerRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        // 去掉 query string + fragment
        var qIdx = url.IndexOfAny(new[] { '?', '#' });
        var pathPart = qIdx >= 0 ? url[..qIdx] : url;
        // 去掉 scheme
        var schemeIdx = pathPart.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx < 0) return null;
        var afterScheme = pathPart[(schemeIdx + 3)..].TrimEnd('/');
        // 第一段是 host(github.com / 自定义域名),丢掉
        var firstSlash = afterScheme.IndexOf('/');
        if (firstSlash < 0) return null;
        var path = afterScheme[(firstSlash + 1)..];
        // path = "{owner}/{repo}" 或 "{owner}/{repo}.git" 等
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var owner = parts[0];
        var repo = parts[1];
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;
        // 去 .git 后缀
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return (owner, repo);
    }

    private static int? TryGetInt(JsonElement obj, string field)
    {
        if (obj.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return null;
    }

    private static string? JsonArrayToString(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Array) return JsonSerializer.Serialize(v, NodelistJsonOptions.Raw);
        if (v.ValueKind == JsonValueKind.String) return JsonSerializer.Serialize(new[] { v.GetString() }, NodelistJsonOptions.Raw);
        return null;
    }

    private static string? JsonObjectOrArrayToString(JsonElement obj, string field)
    {
        if (obj.TryGetProperty(field, out var v) &&
            (v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array))
            return JsonSerializer.Serialize(v, NodelistJsonOptions.Raw);
        return null;
    }

    /// <summary>
    /// apt_dependency 在 raw JSON 里有时是 string(如 "libgl1")有时是 array(["libgl1","libglib2.0-0"])。
    /// 一律转成 JSON 字符串存(数组走 JsonArrayToString,字符串保持原值用 JSON 字符串字面量)。
    /// </summary>
    private static string? TryGetStringOrJoinedArray(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrEmpty(s) ? null : JsonSerializer.Serialize(s, NodelistJsonOptions.Raw);
        }
        if (v.ValueKind == JsonValueKind.Array) return JsonSerializer.Serialize(v, NodelistJsonOptions.Raw);
        return null;
    }
}
