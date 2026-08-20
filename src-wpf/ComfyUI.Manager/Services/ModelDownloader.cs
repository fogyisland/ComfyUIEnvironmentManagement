using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>v0.6.20:streaming model downloader with atomic rename + batch concurrency.
/// 1 ModelVersionEntry = 1 primary file → <modelsDir>/<kind>/<model-slug>-<id8>/<version-slug>-<vid8>/<file>。<br/>
/// 与 v0.6.19 WorkflowDownloader 区别:走 HttpCompletionOption.ResponseHeadersRead + 手工 CopyToAsync,<br/>
/// 支持 GB 级单文件 + IProgress&lt;ModelDownloadProgress&gt; per-byte 流式进度,失败 always-clean .partial。
/// </summary>
public class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    // v0.6.22+:CivitAI API key — 受限 / 标记敏感模型直接调 download URL 返 401/403。
    // token 注入 Authorization: Bearer header(URL ?token= 拼接会泄露到剪贴板历史,
    // 故走 header)。仅在 HTTPS civitai.com 域名下注入 — 镜像 URL 走 HTTP 跳过注入。
    private readonly string _civitaiToken;

    public ModelDownloader(HttpClient http, AppLogger? logger = null, string civitaiToken = "")
    {
        _http = http;
        _logger = logger;
        _civitaiToken = civitaiToken ?? "";
    }

    public async Task<ModelDownloadSummary> DownloadBatchAsync(
        IReadOnlyList<ModelVersionEntry> versions,
        string modelsDir,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sem = new SemaphoreSlim(4);
        var errors = new List<string>();
        int succeeded = 0;
        int failed = 0;
        long totalBytes = 0;

        var tasks = versions.Select(async v =>
        {
            await sem.WaitAsync(ct);
            try
            {
                log?.Report($"[开始] {v.Parent.Title} / {v.Name}");
                var result = await DownloadAsync(v, modelsDir, log, null, ct);
                if (result.Success)
                {
                    Interlocked.Increment(ref succeeded);
                    Interlocked.Add(ref totalBytes, result.SizeBytes);
                    log?.Report($"[✓ OK] {v.Name} → {result.FilePath} ({FormatSize(result.SizeBytes)})");
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    lock (errors) errors.Add($"{v.Name}: {result.FailureReason}");
                    log?.Report($"[✗ FAIL] {v.Name}: {result.FailureReason}");
                }
                return result;
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        return new ModelDownloadSummary
        {
            Succeeded = succeeded,
            Failed = failed,
            TotalBytesDownloaded = totalBytes,
            TotalDuration = sw.Elapsed,
            Errors = errors,
        };
    }

    public async Task<ModelDownloadResult> DownloadAsync(
        ModelVersionEntry version,
        string modelsDir,
        IProgress<string>? log = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var kind = version.Parent.Kind;
        var kindSubfolder = kind.ToComfyUiSubfolder();
        var modelSlugId = ModelKindExtensions.ToSlugId(version.Parent.Title, version.Parent.SourceId);
        var versionSlugId = ModelKindExtensions.ToSlugId(version.Name, version.SourceVersionId);

        var baseDir = Path.Combine(modelsDir, kindSubfolder, modelSlugId);
        var targetDir = ResolveCollisionFree(baseDir, versionSlugId);
        Directory.CreateDirectory(targetDir);

        var primary = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files.FirstOrDefault();
        if (primary is null || string.IsNullOrEmpty(primary.DownloadUrl))
            return new ModelDownloadResult { Success = false, FailureReason = "no primary file" };

        var fileName = string.IsNullOrEmpty(primary.Name) ? "model.safetensors" : primary.Name;
        var finalPath = Path.Combine(targetDir, fileName);
        var partialPath = finalPath + ".partial";

        try
        {
            // v0.6.22+:CivitAI 受限模型需要 token — 仅在 URL 是 HTTPS civitai.com 时
            // 加 Authorization: Bearer header(防镜像 HTTP 泄露)。其他 source(HF / 镜像)的
            // download URL 走默认 _http.GetAsync 不加 header。封装为 helper 让 using 正确 dispose。
            using var resp = await SendCivitAiDownloadAsync(primary.DownloadUrl, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920]; // 80KB buffer for GB efficiency
            long downloaded = 0;
            int read;
            var lastReportBytes = 0L;
            const int reportIntervalBytes = 1_000_000; // report every ~1MB

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                progress?.Report(new ModelDownloadProgress
                {
                    BytesDownloaded = downloaded,
                    TotalBytes = totalBytes,
                });
                if (log is not null && downloaded - lastReportBytes >= reportIntervalBytes)
                {
                    lastReportBytes = downloaded;
                    var pct = totalBytes.HasValue && totalBytes.Value > 0
                        ? (double)downloaded / totalBytes.Value * 100.0
                        : 0.0;
                    log.Report($"  [{pct:F1}%] {FormatSize(downloaded)}/{FormatSize(totalBytes ?? 0)}");
                }
            }

            fileStream.Flush();
            fileStream.Close();

            // Atomic rename
            File.Move(partialPath, finalPath, overwrite: true);

            // Write meta.json sidecar
            var sidecar = new ModelMetaSidecar
            {
                Title = version.Parent.Title,
                Kind = kind,
                BaseModel = version.BaseModel ?? version.Parent.BaseModel,
                Author = version.Parent.Author,
                Source = version.Parent.Source.ToString().ToLowerInvariant(),
                SourceId = version.Parent.SourceId,
                SourceVersionId = version.SourceVersionId,
                SourceUrl = version.Parent.SourceUrl,
                PrimaryFilename = fileName,
                SizeBytes = downloaded,
                NsfwLevel = version.Parent.NsfwLevel ?? 0,
                DownloadedAt = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(
                Path.Combine(targetDir, "meta.json"),
                JsonSerializer.Serialize(sidecar, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() },
                }),
                ct);

            _logger?.Info("model-download", $"OK {finalPath} ({FormatSize(downloaded)})");

            return new ModelDownloadResult
            {
                Success = true,
                FilePath = finalPath,
                SizeBytes = downloaded,
            };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { /* swallow */ }
            _logger?.Error("model-download", $"FAIL {version.Name}: {ex.Message}");
            return new ModelDownloadResult
            {
                Success = false,
                FailureReason = ex.Message,
            };
        }
    }

    /// <summary>v0.6.22+:CivitAI download URL 加 token(受限 / NSFW / 标记敏感模型需要)。
    /// 仅当 URL 是 HTTPS civitai.com 且 _civitaiToken 非空时注入 Authorization: Bearer。
    /// 其他 URL(HF / 镜像 / 普通 HTTP)走默认 GetAsync 不加 header。helper 让 using
    /// 正确 dispose resp,避免 leak。
    /// </summary>
    private async Task<HttpResponseMessage> SendCivitAiDownloadAsync(string downloadUrl, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_civitaiToken)
            && downloadUrl.StartsWith("https://civitai.com/", StringComparison.OrdinalIgnoreCase))
        {
            var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _civitaiToken);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>v0.6.20:collision-free dir name = <baseDir>/<versionSlugId>[/+1/-2...]。
    /// 镜像 v0.6.19 WorkflowDownloader.BuildSubfolderName 行为(短路在 -999 防止无限循环)。
    /// </summary>
    private static string ResolveCollisionFree(string baseDir, string versionSlugId)
    {
        var candidate = Path.Combine(baseDir, versionSlugId);
        if (!Directory.Exists(candidate)) return candidate;
        for (var i = 1; i < 1000; i++)
        {
            var withSuffix = Path.Combine(baseDir, $"{versionSlugId}-{i}");
            if (!Directory.Exists(withSuffix)) return withSuffix;
        }
        throw new IOException($"collision runaway for {versionSlugId} under {baseDir}");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

public class ModelDownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent => TotalBytes.HasValue && TotalBytes.Value > 0
        ? (double)BytesDownloaded / TotalBytes.Value * 100.0
        : 0.0;
}

public class ModelDownloadResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string? FilePath { get; init; }
    public long SizeBytes { get; init; }
}

public class ModelDownloadSummary
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
