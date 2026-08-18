using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:扫描 Settings.WorkflowsDirectory,返回 DownloadedWorkflow 列表。
/// 无 DB — 加/删文件后下次 scan 立即反映。meta.json 缺失或损坏的子目录跳过 + 日志 WARN。</summary>
public class WorkflowFilesystemScanner
{
    private readonly AppLogger? _logger;

    public WorkflowFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>扫描给定目录。无子目录或目录不存在 → 返回空列表。</summary>
    public virtual IReadOnlyList<DownloadedWorkflow> Scan(string workflowsDir)
    {
        if (string.IsNullOrWhiteSpace(workflowsDir) || !Directory.Exists(workflowsDir))
        {
            return Array.Empty<DownloadedWorkflow>();
        }

        var results = new List<DownloadedWorkflow>();
        foreach (var subDir in Directory.EnumerateDirectories(workflowsDir))
        {
            var metaPath = Path.Combine(subDir, "meta.json");
            if (!File.Exists(metaPath))
            {
                _logger?.Warn("workflow-marketplace", $"跳过子目录(无 meta.json): {subDir}");
                continue;
            }

            try
            {
                var metaJson = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<WorkflowMetaSidecar>(
                    metaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta is null)
                {
                    _logger?.Warn("workflow-marketplace", $"meta.json 反序列化返回 null: {metaPath}");
                    continue;
                }

                results.Add(new DownloadedWorkflow
                {
                    SubfolderName = Path.GetFileName(subDir),
                    FullPath = subDir,
                    Title = meta.Title ?? Path.GetFileName(subDir),
                    Source = meta.Source ?? "",
                    SourceId = meta.SourceId ?? "",
                    DownloadedAt = meta.DownloadedAt,
                });
            }
            catch (Exception ex)
            {
                _logger?.Warn("workflow-marketplace",
                    $"meta.json 解析失败 跳过 {subDir}: {ex.Message}");
            }
        }

        return results;
    }
}