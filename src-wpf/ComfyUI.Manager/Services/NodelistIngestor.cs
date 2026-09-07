using System;
using System.Collections.Generic;
using ComfyUI.Manager.Data;
using System.IO;
using System.Text.Json;
using System.Threading;
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
/// 1. 解析 {nodelistDirectory}/nodelist/custom-node-list.json
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

        for (int i = 0; i < toIngest.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var kvp = toIngest[i];
            var author = kvp.Author;
            var repoName = kvp.RepoName;
            progress?.Report(new IngestProgress(i + 1, toIngest.Count, author, repoName));

            // 3a. upsert entry(轻量级先写,失败回滚)
            try
            {
                _repo.UpsertEntry(author, repoName, source: "json");
                newCount++;
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"entry upsert failed for {author}/{repoName}: {ex.Message}");
                continue;
            }

            // 3b. 拉云端 metadata → upsert detail
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
                        meta.License, meta.DefaultBranch,
                        meta.UpdatedAt?.ToString("o"), meta.RawJson, meta.Host,
                        DateTime.UtcNow.ToString("o")));
                    detailsWritten++;
                }
                else
                {
                    detailsFailed++;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-ingest", $"detail fetch failed for {author}/{repoName}: {ex.Message}");
                detailsFailed++;
            }
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
