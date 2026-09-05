using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-directory:扫描用户配置的
/// NodelistDirectory 目录下所有 custom-node-list.json 文件,提取
/// author/title(节点 owner/name),调 GitHubVersionService 拉 metadata,
/// 写 scanned_nodes 表。
/// </summary>
public sealed class NodeListScanner
{
    private readonly NodeRepository _nodeRepo;
    private readonly GitHubVersionService _versionService;
    private readonly AppLogger? _logger;

    public NodeListScanner(NodeRepository nodeRepo, GitHubVersionService versionService, AppLogger? logger = null)
    {
        _nodeRepo = nodeRepo;
        _versionService = versionService;
        _logger = logger;
    }

    public sealed record ScanResult(
        int FilesScanned,
        int TotalEntries,
        int UniqueNodes,
        int NewNodes,
        int UpdatedNodes,
        int SkippedDuplicates,
        int FetchFailures,
        TimeSpan Elapsed);

    public sealed record ScanProgress(
        int FilesScanned,
        int CurrentFileIndex,
        string CurrentFile,
        int UniqueNodes);

    public async Task<ScanResult> ScanAsync(
        string directory,
        IProgress<ScanProgress>? progress = null,
        string? githubToken = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new ScanResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);

        // 1. 递归找所有 custom-node-list.json
        var files = Directory.EnumerateFiles(directory, "custom-node-list.json", SearchOption.AllDirectories).ToList();

        // 2+3. 解析 + HashSet 去重
        // Key = author/title,Value = (reference, description, sourceFile)
        var nodes = new Dictionary<string, (string Reference, string Description, string SourceFile)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("custom_nodes", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in arr.EnumerateArray())
                {
                    if (!entry.TryGetProperty("author", out var authorEl) ||
                        !entry.TryGetProperty("title", out var titleEl))
                        continue;
                    var author = authorEl.GetString() ?? "";
                    var title = titleEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(title))
                        continue;
                    var key = $"{author}/{title}";
                    var reference = entry.TryGetProperty("reference", out var refEl) ? (refEl.GetString() ?? "") : "";
                    var description = entry.TryGetProperty("description", out var descEl) ? (descEl.GetString() ?? "") : "";
                    if (!nodes.ContainsKey(key))
                        nodes[key] = (reference, description, file);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("nodelist-scan", $"parse failed for {file}: {ex.Message}");
            }
        }
        if (nodes.Count == 0)
        {
            sw.Stop();
            return new ScanResult(files.Count, 0, 0, 0, 0, 0, 0, sw.Elapsed);
        }

        // 4. 拉 GitHub metadata
        var idList = nodes.Select(kv => (kv.Key, kv.Value.Reference)).ToList();
        var versions = await _versionService.FetchVersionsAsync(idList, githubToken, ct: ct);

        // 5. 写 scanned_nodes
        int newCount = 0, updatedCount = 0, failCount = 0;
        var nowIso = DateTime.UtcNow.ToString("o");
        foreach (var (key, (reference, description, sourceFile)) in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var parts = key.Split('/', 2);
            var author = parts[0];
            var title = parts[1];
            var version = "";
            string? publishedAt = null;
            if (versions.TryGetValue(key, out var infos) && infos.Count > 0)
            {
                var stable = infos.FirstOrDefault(v => !v.IsPrerelease) ?? infos[0];
                version = stable.Tag ?? "";
                publishedAt = stable.PublishedAt;
            }
            else
            {
                failCount++;
            }

            var existing = _nodeRepo.Get(key);
            var node = new ScannedNode
            {
                Id = key,
                EnvId = "global",
                Package = title,
                PackagePath = sourceFile,
                Version = version,
                Author = author,
                Description = description,
                Status = string.IsNullOrEmpty(version) ? "pending" : "ok",
                ScanMeta = new Dictionary<string, string>
                {
                    ["source"] = "nodelist",
                    ["reference"] = reference,
                    ["published_at"] = publishedAt ?? "",
                },
                LastScannedAt = nowIso,
                Source = "nodelist",
                RepositoryUrl = reference,
            };
            _nodeRepo.Upsert(node);
            if (existing is null) newCount++;
            else updatedCount++;
        }

        sw.Stop();
        return new ScanResult(
            files.Count, nodes.Count, nodes.Count,
            newCount, updatedCount, 0, failCount, sw.Elapsed);
    }
}
