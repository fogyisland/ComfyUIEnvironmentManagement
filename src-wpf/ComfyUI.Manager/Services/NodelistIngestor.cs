using System;
using System.Collections.Generic;
using ComfyUI.Manager.Data;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using System.Linq;

namespace ComfyUI.Manager.Services;

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

    public sealed record IngestProgress(
        int Current,
        int Total,
        string Author,
        string RepoName);

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
        IProgress<IngestProgress>? progress = null,
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

        // 2. 提取 author+repo_name(去重)
        var pairs = new HashSet<(string Author, string RepoName)>();
        foreach (var entry in arr.EnumerateArray())
        {
            if (!entry.TryGetProperty("author", out var a) ||
                !entry.TryGetProperty("title", out var t)) continue;
            var author = a.GetString();
            var repo = t.GetString();
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(repo)) continue;
            pairs.Add((author!, repo!));
        }

        // 3. 对比数据库现有 keys,只 upsert 新增(增量)
        var existing = forceFull ? new HashSet<(string, string)>() : _repo.GetExistingKeys();
        var toIngest = pairs.Where(p => existing.Add(p)).ToList();
        var newCount = 0;
        var detailsWritten = 0;
        var detailsFailed = 0;

        // v1.0.0.x (2026-09-05) feat/nodelist-directory:用户原话"平均提交的频率是一秒钟10个" —
        // 用 System.Threading.Channels.Channel + SemaphoreSlim 限流 10 req/s,Task.WhenAll 并发 fetch。
        // 流程:先把 toIngest 写到 channel 缓冲,后端 N=10 个 worker 拉 + 限流 100ms/req + upsert。
        var rateLimit = new SemaphoreSlim(1, 1);
        var lastRelease = DateTime.UtcNow;
        var rateDelay = TimeSpan.FromMilliseconds(100);  // 1s/10req = 100ms/req

        async Task<((int NewCount, int DetailFailed, int DetailWritten) Result, int Index)> ProcessOneAsync(
            (string Author, string RepoName) kvp, int index, int total, int currentNew)
        {
            ct.ThrowIfCancellationRequested();
            var author = kvp.Author;
            var repoName = kvp.RepoName;
            // 报告进度(主线程)— 用 Application.Current.Dispatcher 跨线程
            try
            {
                if (System.Windows.Application.Current?.Dispatcher is { } d)
                {
                    d.Invoke(() => progress?.Report(new IngestProgress(index + 1, total, author, repoName)));
                }
            }
            catch { }

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
            // 3a. upsert entry
            try
            {
                _repo.UpsertEntry(author, repoName, source: "json");
                localNew = 1;
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"entry upsert failed for {author}/{repoName}: {ex.Message}");
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
                }
                else
                {
                    localDetailFailed = 1;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"detail fetch failed for {author}/{repoName}: {ex.Message}");
                localDetailFailed = 1;
            }
            return ((localNew, localDetail, localDetailFailed), currentNew + localNew);
        }

        // 10 个并发 worker 拉数据
        var tasks = new List<Task<((int, int, int), int)>>();
        var currentNew = 0;
        for (int i = 0; i < toIngest.Count; i++)
        {
            tasks.Add(ProcessOneAsync(toIngest[i], i, toIngest.Count, currentNew));
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
            var r = tup.Item1;
            newCount += r.Item1;
            detailsWritten += r.Item2;
            detailsFailed += r.Item3;
        }

        sw.Stop();
        return new IngestResult(
            pairs.Count,
            newCount,
            pairs.Count - toIngest.Count,
            detailsWritten,
            detailsFailed,
            sw.Elapsed);
    }

    private Microsoft.Data.Sqlite.SqliteConnection OpenConn()
    {
        // 借用 _repo 的 factory
        var f = typeof(NodelistRepository).GetField("_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_repo) as SqliteConnectionFactory;
        return f?.Open() ?? throw new InvalidOperationException("SqliteConnectionFactory missing");
    }
}
