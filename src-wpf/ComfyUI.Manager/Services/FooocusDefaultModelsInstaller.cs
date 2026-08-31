using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01):Fooocus 默认模型下载 installer ——
/// 用户 dev build Fooocus env 启动 fail:`launch.py` line 145 自动从
/// <c>huggingface.co</c> 下载 4 个 vae_approx / fooocus_expansion 模型,
/// 网络超时(<c>WinError 10060</c>)直接 crash env(exit code 1)。
///
/// 本 installer 让用户在 env 启动前主动下载,失败可重试;装完后 env 启动
/// 时 <c>download_models()</c> 跳过已存在的文件(或 Fooocus launcher
/// 自己的 is_installed 检查通过)。
///
/// **设计决策**(T22 plan 2026-09-01):
/// <list type="bullet">
///   <item>4 个文件并发下载(镜像 <see cref="ModelDownloader"/> 模式,
///   SemaphoreSlim(4) + HttpClient 流式 + .partial → final 原子 rename)</item>
///   <item>HF 镜像支持:复用 <see cref="ModelSourceFactory.ResolveBaseUrl"/>
///   (Settings.ModelSourceHuggingFaceUseMirror + MirrorUrl),镜像 Fooocus
///   上游 <c>model_loader.py</c> line 18 的 <c>HF_MIRROR</c> 替换逻辑</item>
///   <item>HTTP 代理支持:复用 <see cref="HttpProxyConfig"/> +
///   <see cref="App.BuildHttpClient"/> 60s timeout + User-Agent</item>
///   <item>结果类型 <see cref="FooocusModelsDownloadResult"/> 实现
///   <see cref="IBedInstallResult"/> —— 跟 FooocusBaseEnvInstaller /
///   ForgeBaseEnvInstaller 同 pattern,<see cref="BaseEnvStatusViewModel"/>
///   通用 ctor 直接接(Func delegate)</item>
/// </list>
/// </summary>
public class FooocusDefaultModelsInstaller
{
    private const int ConcurrentDownloads = 4;  // 镜像 ModelDownloader.SemaphoreSlim(4)
    private const int DownloadBufferSize = 81920;  // 80KB buffer for GB efficiency
    private const int ProgressLogIntervalBytes = 1_000_000;  // log every ~1MB

    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly HttpProxyConfig? _proxy;
    private readonly Settings? _settings;

    public FooocusDefaultModelsInstaller(
        HttpClient? http = null,
        AppLogger? logger = null,
        HttpProxyConfig? proxy = null,
        Settings? settings = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _logger = logger;
        _proxy = proxy;
        _settings = settings;
    }

    /// <summary>
    /// 检查 Fooocus 默认模型是否已下载(marker 文件存在)。镜像
    /// <see cref="FooocusBaseEnvInstaller.IsInstalled"/>。
    /// </summary>
    public static bool IsInstalled(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        return File.Exists(Path.Combine(env.RootPath,
            FooocusDefaultModelsConstants.MarkerFileName));
    }

    /// <summary>
    /// 下载 4 个 Fooocus 默认模型 + 写 marker。失败任一 → 返 Fail,
    /// 已成功的文件保留(env 启动可复用);用户可重试按钮(剩余失败会重下)。
    /// </summary>
    public virtual async Task<FooocusModelsDownloadResult> InstallAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        var sw = Stopwatch.StartNew();
        logProgress?.Report($"[fooocus-models] env='{env.Name}' 开始下载 4 个 Fooocus 默认模型");

        var sem = new SemaphoreSlim(ConcurrentDownloads);
        var successes = 0;
        var failures = 0;
        var totalBytes = 0L;
        var errors = new List<string>();
        var tasks = new List<Task>();

        foreach (var entry in FooocusDefaultModelsConstants.DefaultModels)
        {
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await DownloadSingleAsync(env, entry, logProgress, ct);
                    if (result.Success)
                    {
                        Interlocked.Increment(ref successes);
                        Interlocked.Add(ref totalBytes, result.SizeBytes);
                    }
                    else
                    {
                        Interlocked.Increment(ref failures);
                        lock (errors) errors.Add($"{entry.FileName}: {result.FailureReason}");
                        logProgress?.Report($"[fooocus-models] ✗ {entry.FileName}: {result.FailureReason}");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    lock (errors) errors.Add($"{entry.FileName}: {ex.Message}");
                    logProgress?.Report($"[fooocus-models] ✗ {entry.FileName}: 异常 {ex.Message}");
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (failures == 0)
        {
            // 全部成功 → 写 marker
            var markerPath = Path.Combine(env.RootPath, FooocusDefaultModelsConstants.MarkerFileName);
            try
            {
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }
            catch (Exception ex)
            {
                _logger?.Warn("fooocus-models", $"env='{env.Name}' marker 写失败(继续):{ex.Message}");
            }

            _logger?.Info("fooocus-models",
                $"env='{env.Name}' 默认模型下载完成(success={successes}/{FooocusDefaultModelsConstants.DefaultModels.Count} bytes={FormatBytes(totalBytes)} elapsed={sw.Elapsed})");
            logProgress?.Report($"[fooocus-models] ✓ 完成({successes}/{FooocusDefaultModelsConstants.DefaultModels.Count} 文件,total {FormatBytes(totalBytes)},elapsed {sw.Elapsed:mm\\:ss})");
            return new FooocusModelsDownloadResult(
                Success: true, Cancelled: false, Reason: null,
                DownloadedCount: successes, FailedCount: failures, TotalBytes: totalBytes);
        }

        var reason = $"下载失败 {failures}/{FooocusDefaultModelsConstants.DefaultModels.Count}:" + string.Join("; ", errors);
        logProgress?.Report($"[fooocus-models] ✗ {reason}");
        return new FooocusModelsDownloadResult(
            Success: false, Cancelled: false, Reason: reason,
            DownloadedCount: successes, FailedCount: failures, TotalBytes: totalBytes);
    }

    /// <summary>
    /// 单文件下载 + 原子 .partial → final rename。镜像 ModelDownloader.DownloadAsync
    /// (line 108-151)的 pattern,但简化为:
    /// - 不写 meta.json sidecar(Fooocus 不读)
    /// - 不走 CivitAI token 注入(Fooocus URL 不需要)
    /// - 简化 fallback:失败返 failure reason 而不是 throw
    /// </summary>
    private async Task<SingleDownloadResult> DownloadSingleAsync(
        Environment env,
        FooocusModelEntry entry,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        var targetDir = Path.Combine(env.RootPath, entry.SubDir);
        var finalPath = Path.Combine(targetDir, entry.FileName);
        var partialPath = finalPath + ".partial";

        // 跳过已存在文件(同 ModelDownloader behavior)
        if (File.Exists(finalPath))
        {
            logProgress?.Report($"[fooocus-models] ✓ {entry.FileName} 已存在(跳过)");
            return new SingleDownloadResult(true, new FileInfo(finalPath).Length);
        }

        Directory.CreateDirectory(targetDir);

        var resolvedUrl = ResolveMirror(entry.Url, _settings);
        logProgress?.Report($"[fooocus-models] ↓ {entry.FileName} <- {resolvedUrl}");

        try
        {
            using var resp = await _http.GetAsync(resolvedUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[DownloadBufferSize];
            long downloaded = 0;
            int read;
            var lastReportBytes = 0L;

            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                if (downloaded - lastReportBytes >= ProgressLogIntervalBytes)
                {
                    lastReportBytes = downloaded;
                    var pct = totalBytes > 0 ? (double)downloaded / totalBytes * 100.0 : 0.0;
                    logProgress?.Report($"[fooocus-models]   {entry.FileName} [{pct:F1}%] {FormatBytes(downloaded)}/{FormatBytes(totalBytes)}");
                }
            }

            fileStream.Flush();
            fileStream.Close();

            // Atomic rename .partial → final
            File.Move(partialPath, finalPath, overwrite: true);

            logProgress?.Report($"[fooocus-models] ✓ {entry.FileName} {FormatBytes(downloaded)}");
            return new SingleDownloadResult(true, downloaded);
        }
        catch (Exception ex)
        {
            // 清理 partial 文件,失败可重试
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            return new SingleDownloadResult(false, 0L, ex.Message);
        }
    }

    /// <summary>
    /// 镜像 Fooocus 上游 <c>model_loader.py</c> line 18:<c>HF_MIRROR</c>
    /// 字符串替换。镜像 URL 走 <see cref="ModelSourceFactory.HuggingFaceOfficial"/>
    /// + Settings.ModelSourceHuggingFaceMirrorUrl(<see cref="ModelSourceFactory.ResolveBaseUrl"/>
    /// 是 private,inline 同款逻辑)。
    /// </summary>
    private static string ResolveMirror(string rawUrl, Settings? settings)
    {
        if (settings is null) return rawUrl;
        var official = ModelSourceFactory.HuggingFaceOfficial.TrimEnd('/');
        var useMirror = settings.ModelSourceHuggingFaceUseMirror;
        var mirrorUrl = settings.ModelSourceHuggingFaceMirrorUrl;
        var mirrorBase = useMirror && !string.IsNullOrWhiteSpace(mirrorUrl)
            ? mirrorUrl.TrimEnd('/')
            : official;
        if (string.Equals(mirrorBase, official, StringComparison.OrdinalIgnoreCase)) return rawUrl;
        return rawUrl.Replace(official, mirrorBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private record SingleDownloadResult(bool Success, long SizeBytes, string? FailureReason = null);
}

/// <summary>
/// v1.0.0.x (2026-09-01):Fooocus 默认模型下载结果 —— 跟 <see cref="FooocusBedInstallResult"/>
/// 同 pattern 实现 <see cref="IBedInstallResult"/>(让 <see cref="BaseEnvStatusViewModel"/>
/// 通用 ctor 直接接)。
///
/// 额外字段(DownloadedCount / FailedCount / TotalBytes)用于 inline status panel
/// 显示"下载 X/Y MB / Z 个文件"详细状态。
/// </summary>
public record FooocusModelsDownloadResult(
    bool Success,
    bool Cancelled,
    string? Reason,
    int DownloadedCount,
    int FailedCount,
    long TotalBytes) : IBedInstallResult;
