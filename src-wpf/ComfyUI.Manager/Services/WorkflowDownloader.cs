using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.19:下载 workflow.json + preview 到 Settings.WorkflowsDirectory。
/// 单条 + 批量(SemaphoreSlim=4 并发)。每个 subfolder 写 workflow.json + preview.<ext> + meta.json。</summary>
public class WorkflowDownloader
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;

    public WorkflowDownloader(HttpClient http, AppLogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public virtual async Task<WorkflowDownloadResult> DownloadAsync(
        WorkflowEntry entry, string workflowsDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowsDir))
            return WorkflowDownloadResult.Fail("workflows dir is empty");
        if (entry is null || string.IsNullOrEmpty(entry.WorkflowJsonUrl))
            return WorkflowDownloadResult.Fail("entry or json url is empty");

        try
        {
            Directory.CreateDirectory(workflowsDir);

            var subfolderName = BuildSubfolderName(entry, workflowsDir);
            var subfolderPath = Path.Combine(workflowsDir, subfolderName);
            Directory.CreateDirectory(subfolderPath);

            log?.Report($"[{entry.Source}] 开始下载:{entry.Title}");
            _logger?.Info("workflow-download",
                $"start entry='{entry.SourceId}' title='{entry.Title}' subfolder='{subfolderName}'");

            // 1. workflow.json
            var jsonBytes = await _http.GetByteArrayAsync(entry.WorkflowJsonUrl, ct).ConfigureAwait(false);
            // pretty-print if valid JSON, else write raw
            try
            {
                var doc = JsonDocument.Parse(jsonBytes);
                using var fs = File.Create(Path.Combine(subfolderPath, "workflow.json"));
                await JsonSerializer.SerializeAsync(fs, doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await File.WriteAllBytesAsync(Path.Combine(subfolderPath, "workflow.json"), jsonBytes, ct).ConfigureAwait(false);
                _logger?.Warn("workflow-download",
                    $"workflow.json not valid JSON; wrote raw entry='{entry.SourceId}'");
            }

            // 2. preview (best-effort)
            var previewPath = await TryDownloadPreviewAsync(entry, subfolderName, subfolderPath, ct).ConfigureAwait(false);

            // 3. meta.json sidecar
            var meta = new WorkflowMetaSidecar
            {
                Title = entry.Title,
                Source = entry.Source.ToString(),
                SourceId = entry.SourceId,
                DownloadedAt = DateTime.UtcNow,
            };
            // augment with extra fields via serialization — use anonymous helper
            var metaJson = JsonSerializer.Serialize(new
            {
                title = entry.Title,
                description = entry.Description,
                author = entry.Author,
                source = entry.Source.ToString(),
                source_id = entry.SourceId,
                source_url = entry.SourceUrl,
                workflow_json_url = entry.WorkflowJsonUrl,
                preview_image_url = entry.PreviewImageUrl,
                tags = entry.Tags,
                downloaded_at = meta.DownloadedAt,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(subfolderPath, "meta.json"), metaJson, ct).ConfigureAwait(false);

            log?.Report($"[{entry.Source}] ✓ OK saved to {subfolderName}");
            _logger?.Info("workflow-download", $"ok entry='{entry.SourceId}' path='{subfolderPath}'");
            return WorkflowDownloadResult.Ok(subfolderPath);
        }
        catch (Exception ex)
        {
            var reason = ex.Message;
            log?.Report($"[{entry.Source}] ✗ FAIL {reason}");
            _logger?.Error("workflow-download", $"failed entry='{entry.SourceId}'", ex);
            return WorkflowDownloadResult.Fail(reason);
        }
    }

    public virtual async Task<WorkflowBatchSummary> DownloadBatchAsync(
        IEnumerable<WorkflowEntry> entries, string workflowsDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var entryList = entries?.ToList() ?? new List<WorkflowEntry>();
        if (entryList.Count == 0)
        {
            log?.Report("[批量下载] 无选中项");
            return new WorkflowBatchSummary { Succeeded = 0, Failed = 0, Errors = Array.Empty<string>() };
        }

        log?.Report($"[批量下载] 开始 N={entryList.Count}");
        using var sem = new SemaphoreSlim(4);
        var tasks = entryList.Select(async e =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { return await DownloadAsync(e, workflowsDir, log, ct).ConfigureAwait(false); }
            finally { sem.Release(); }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var succeeded = results.Count(r => r.Success);
        var failed = results.Length - succeeded;
        var errors = results.Where(r => !r.Success && r.FailureReason != null)
            .Select(r => $"{r.FailureReason}").ToArray();

        log?.Report($"[批量下载完成] 成功 {succeeded} / 失败 {failed}");
        return new WorkflowBatchSummary
        {
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors,
        };
    }

    private string BuildSubfolderName(WorkflowEntry entry, string workflowsDir)
    {
        var slug = Slugify(entry.Title);
        if (string.IsNullOrEmpty(slug)) slug = "workflow";
        var id8 = (entry.SourceId ?? "").Length >= 8
            ? entry.SourceId.Substring(0, 8)
            : (entry.SourceId ?? "00000000").PadRight(8, '0');
        var baseName = $"{slug}-{id8}";

        var candidate = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(workflowsDir, candidate)))
        {
            candidate = $"{baseName}-{suffix}";
            suffix++;
        }
        return candidate;
    }

    private static string Slugify(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length);
        var lastDash = false;
        foreach (var ch in input.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')
            {
                sb.Append(ch);
                lastDash = ch == '-';
            }
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || ch == '_')
            {
                if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
        }
        // trim trailing dash
        while (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
        return sb.ToString();
    }

    private async Task<string?> TryDownloadPreviewAsync(
        WorkflowEntry entry, string subfolderName, string subfolderPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.PreviewImageUrl)) return null;
        try
        {
            var ext = Path.GetExtension(new Uri(entry.PreviewImageUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
            var fileName = $"{subfolderName}.preview{ext}";
            var bytes = await _http.GetByteArrayAsync(entry.PreviewImageUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(subfolderPath, fileName), bytes, ct).ConfigureAwait(false);
            return fileName;
        }
        catch (Exception ex)
        {
            _logger?.Warn("workflow-download",
                $"preview failed entry='{entry.SourceId}': {ex.Message}");
            return null;
        }
    }
}

public class WorkflowDownloadResult
{
    public bool Success { get; init; }
    public string? SubfolderPath { get; init; }
    public string? FailureReason { get; init; }

    public static WorkflowDownloadResult Ok(string path) => new() { Success = true, SubfolderPath = path };
    public static WorkflowDownloadResult Fail(string reason) => new() { Success = false, FailureReason = reason };
}

public class WorkflowBatchSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
