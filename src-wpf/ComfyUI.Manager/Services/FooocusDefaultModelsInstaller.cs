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
    /// v1.0.0.x (2026-09-01) T24:精确判定 Fooocus 全部默认模型是否下载 ——
    /// T22 4 vae_approx 文件 + T23b probe 拿 4 dict(launcher 自动下载)→ 全部
    /// 文件存在 → 返 true(merged 「下载默认模型」按钮 disabled)。否则 false
    /// (按钮 enabled,缺哪个装哪个)。
    /// <para>3 步:</para>
    /// <list type="number">
    ///   <item>T22 4 fixed file existence check(<c>models/vae_approx/...</c> +
    ///   <c>models/prompt_expansion/fooocus_expansion/pytorch_model.bin</c>)</item>
    ///   <item>Step 1 全部存在 → spawn venv Python 跑 <see cref="FooocusConfigProbe"/>
    ///   拿 4 dict + 5 path</item>
    ///   <item>遍历 4 dict 所有 entry,check 实际文件存在 → 全部存在 → 返 true</item>
    /// </list>
    /// <para>probe 失败(venv python 不存在 / JSON parse fail)→ 返 false
    /// (按钮保持 enabled,用户重试或诊断环境)。</para>
    /// </summary>
    public static async Task<bool> CheckAllDefaultModelsDownloadedAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;

        // Step 1: T22 4 fixed vae_approx + fooocus_expansion 文件
        var vaeApproxDir = Path.Combine(env.RootPath, "models", "vae_approx");
        var vaeApproxFiles = new[]
        {
            "xlvaeapp.pth",
            "vaeapp_sd15.pth",          // Fooocus 上游 quirk:URL .pt,本地 .pth
            "xl-to-v1_interposer-v4.0.safetensors",
        };
        foreach (var name in vaeApproxFiles)
        {
            if (!File.Exists(Path.Combine(vaeApproxDir, name)))
            {
                logProgress?.Report($"[fooocus-check] 缺 T22 文件:{Path.Combine(vaeApproxDir, name)}");
                return false;
            }
        }
        var expansionPath = Path.Combine(env.RootPath, "models", "prompt_expansion", "fooocus_expansion", "pytorch_model.bin");
        if (!File.Exists(expansionPath))
        {
            logProgress?.Report($"[fooocus-check] 缺 T22 文件:{expansionPath}");
            return false;
        }

        // Step 2: probe 4 dict
        var config = await FooocusConfigProbe.ProbeAsync(env, logProgress, ct).ConfigureAwait(false);
        if (config is null)
        {
            logProgress?.Report("[fooocus-check] probe 失败 → 按钮保持 enabled");
            return false;
        }

        // Step 3: 遍历 4 dict 所有 entry
        var entries = new List<(string FileName, string SubDir)>();
        AddDictFileNames(entries, config.CheckpointDownloads, config.Paths.GetValueOrDefault("checkpoints", "models/checkpoints"));
        AddDictFileNames(entries, config.LoraDownloads, config.Paths.GetValueOrDefault("loras", "models/loras"));
        AddDictFileNames(entries, config.EmbeddingsDownloads, config.Paths.GetValueOrDefault("embeddings", "models/embeddings"));
        AddDictFileNames(entries, config.VaeDownloads, config.Paths.GetValueOrDefault("vae", "models/vae"));

        foreach (var entry in entries)
        {
            var path = Path.Combine(env.RootPath, entry.SubDir, entry.FileName);
            if (!File.Exists(path))
            {
                logProgress?.Report($"[fooocus-check] 缺 T23b 文件:{path}");
                return false;
            }
        }

        logProgress?.Report($"[fooocus-check] ✓ 全部 {entries.Count + 4} 个默认模型已就位(按钮 disabled)");
        return true;
    }

    private static void AddDictFileNames(
        List<(string FileName, string SubDir)> sink,
        IReadOnlyDictionary<string, string> dict,
        string subDir)
    {
        foreach (var kvp in dict)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
            sink.Add((kvp.Key, subDir));
        }
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

    /// <summary>
    /// v1.0.0.x (2026-09-01) T23b:Fooocus launcher 全部 5 类 dict 下载 ——
    /// 镜像 <see cref="InstallAsync"/> 模式但下 launcher 启动时自动下载的
    /// checkpoint_downloads + lora_downloads + embeddings_downloads + vae_downloads
    /// 4 dict 的所有 entry(可能含 SDXL 5GB checkpoint)。<see cref="FooocusConfigProbe"/>
    /// 读 Fooocus config.py 拿 4 dict + 5 path,WPF 端预下避免 launch.py line 131-140
    /// 网络超时 crash env。
    /// </summary>
    public virtual async Task<FooocusModelsDownloadResult> DownloadLauncherDefaultsAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        var sw = Stopwatch.StartNew();
        logProgress?.Report("[fooocus-launcher-models] env='{env.Name}' probe Fooocus config.py...");

        var config = await FooocusConfigProbe.ProbeAsync(env, logProgress, ct).ConfigureAwait(false);
        if (config is null)
        {
            return new FooocusModelsDownloadResult(
                Success: false, Cancelled: false, Reason: "Fooocus config probe 失败",
                DownloadedCount: 0, FailedCount: 0, TotalBytes: 0);
        }

        // 5 类条目 → 统一 (file_name, url, sub_dir) 喂现有 DownloadSingleAsync
        var entries = new List<(string FileName, string Url, string SubDir)>();
        AddDictEntries(entries, config.CheckpointDownloads, config.Paths.GetValueOrDefault("checkpoints", "models/checkpoints"));
        AddDictEntries(entries, config.LoraDownloads, config.Paths.GetValueOrDefault("loras", "models/loras"));
        AddDictEntries(entries, config.EmbeddingsDownloads, config.Paths.GetValueOrDefault("embeddings", "models/embeddings"));
        AddDictEntries(entries, config.VaeDownloads, config.Paths.GetValueOrDefault("vae", "models/vae"));

        if (entries.Count == 0)
        {
            logProgress?.Report("[fooocus-launcher-models] 4 dict 都是空(preset 干净),无需下载");
            // 写 marker 标记"检查过不需要下"—— 跟 InstallAsync success 行为对齐
            WriteMarker(env);
            return new FooocusModelsDownloadResult(
                Success: true, Cancelled: false, Reason: null,
                DownloadedCount: 0, FailedCount: 0, TotalBytes: 0);
        }

        logProgress?.Report($"[fooocus-launcher-models] 共 {entries.Count} 个 launcher 默认模型待下载");
        var sem = new SemaphoreSlim(ConcurrentDownloads);
        var successes = 0;
        var failures = 0;
        var totalBytes = 0L;
        var errors = new List<string>();
        var tasks = new List<Task>();

        foreach (var entry in entries)
        {
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await DownloadSingleAsync(
                        env,
                        new FooocusModelEntry(entry.FileName, entry.Url, entry.SubDir),
                        logProgress, ct);
                    if (result.Success)
                    {
                        Interlocked.Increment(ref successes);
                        Interlocked.Add(ref totalBytes, result.SizeBytes);
                    }
                    else
                    {
                        Interlocked.Increment(ref failures);
                        lock (errors) errors.Add($"{entry.FileName}: {result.FailureReason}");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    lock (errors) errors.Add($"{entry.FileName}: {ex.Message}");
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
            WriteMarker(env);
            _logger?.Info("fooocus-launcher-models",
                $"env='{env.Name}' launcher 默认模型下载完成(success={successes}/{entries.Count} bytes={FormatBytes(totalBytes)} elapsed={sw.Elapsed})");
            logProgress?.Report($"[fooocus-launcher-models] ✓ 完成({successes}/{entries.Count},total {FormatBytes(totalBytes)})");
            return new FooocusModelsDownloadResult(
                Success: true, Cancelled: false, Reason: null,
                DownloadedCount: successes, FailedCount: failures, TotalBytes: totalBytes);
        }

        var reason = $"下载失败 {failures}/{entries.Count}:" + string.Join("; ", errors);
        logProgress?.Report($"[fooocus-launcher-models] ✗ {reason}");
        return new FooocusModelsDownloadResult(
            Success: false, Cancelled: false, Reason: reason,
            DownloadedCount: successes, FailedCount: failures, TotalBytes: totalBytes);
    }

    private static void AddDictEntries(
        List<(string FileName, string Url, string SubDir)> sink,
        IReadOnlyDictionary<string, string> dict,
        string subDir)
    {
        foreach (var kvp in dict)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value)) continue;
            sink.Add((kvp.Key, kvp.Value, subDir));
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-09-01) T28:Fooocus <c>fooocus_expansion</c> 元数据文件
    /// pre-step download —— T22 只下了 <c>pytorch_model.bin</c>(351MB),
    /// 但 <c>extras/expansion.py</c> line 39 <c>AutoTokenizer.from_pretrained()</c>
    /// + line 62 <c>AutoModelForCausalLM.from_pretrained()</c> 需要 HF repo 的
    /// 6 个元数据文件(<c>config.json</c> / <c>tokenizer_config.json</c> /
    /// <c>special_tokens_map.json</c> / <c>vocab.json</c> / <c>merges.txt</c>
    /// / <c>positive.txt</c>)。否则 pipeline init 抛
    /// <c>OSError: ... does not appear to have a file named config.json</c> →
    /// async_worker thread crash → Fooocus 启动后所有 prompt 都没 prompt expansion。
    ///
    /// <para><b>调用入口</b>:ProcessLauncher.StartEnvAsync Fooocus kind env 启动前
    /// pre-step,idempotent — 文件已存在则跳过,best-effort(fail 不阻塞 launch,
    /// 只 logProgress warn)。镜像 <see cref="CheckAllDefaultModelsDownloadedAsync"/>
    /// 的 static pattern + Settings 模型镜像 URL 复用
    /// <see cref="ResolveMirror"/>。</para>
    /// </summary>
    /// <returns>true = 全部 6 个文件都在(可能本次下完 或 之前已下);false = 有缺失或失败</returns>
    public static Task<bool> EnsureExpansionMetadataAsync(
        Environment env,
        Settings? settings,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
        => EnsureExpansionMetadataAsync(env, settings, http: null, logProgress, ct);

    /// <summary>
    /// 重载:测试可注入 <paramref name="http"/> 用 stub handler(测试 seam,
    /// 镜像 <see cref="InstallAsync"/> line 58 的 <c>http ?? new HttpClient { ... }</c> pattern)。
    /// </summary>
    public static async Task<bool> EnsureExpansionMetadataAsync(
        Environment env,
        Settings? settings,
        HttpClient? http,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath))
        {
            logProgress?.Report("[fooocus-expansion-meta] ✗ env / RootPath 为空");
            return false;
        }

        var targetDir = Path.Combine(env.RootPath,
            FooocusExpansionMetadataConstants.TargetSubDir);
        Directory.CreateDirectory(targetDir);

        var fileNames = FooocusExpansionMetadataConstants.MetadataFileNames;

        // Step 1: 快速探测 — 全部已存在就直接返 true,不浪费 HttpClient
        var missing = new List<string>();
        foreach (var name in fileNames)
        {
            if (!File.Exists(Path.Combine(targetDir, name))) missing.Add(name);
        }
        if (missing.Count == 0)
        {
            logProgress?.Report($"[fooocus-expansion-meta] ✓ 全部 {fileNames.Count} 个元数据已就位(跳过 download)");
            return true;
        }
        logProgress?.Report($"[fooocus-expansion-meta] 缺 {missing.Count}/{fileNames.Count} 个元数据:{string.Join(", ", missing)}");

        // Step 2: 并发下缺失的文件(镜像 InstallAsync SemaphoreSlim(4) + .partial pattern)
        // 测试可注入 stub http;否则 new 内部短 timeout 5min HttpClient
        var ownsHttp = http is null;
        var httpToUse = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var sem = new SemaphoreSlim(ConcurrentDownloads);
        var successes = 0;
        var failures = 0;
        var tasks = new List<Task>();

        try
        {
            foreach (var fileName in missing)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        var ok = await DownloadExpansionMetadataSingleAsync(
                            httpToUse, targetDir, fileName, settings, logProgress, ct);
                        if (ok) Interlocked.Increment(ref successes);
                        else Interlocked.Increment(ref failures);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // best-effort:个别文件失败 → successes 已 count,下面按 successes 判断
            }
        }
        finally
        {
            if (ownsHttp) httpToUse.Dispose();
        }

        var allOk = failures == 0;
        if (allOk)
        {
            logProgress?.Report($"[fooocus-expansion-meta] ✓ 完成({successes}/{missing.Count})");
        }
        else
        {
            logProgress?.Report($"[fooocus-expansion-meta] ⚠ 部分失败({successes}/{missing.Count}),Fooocus 启动后 prompt expansion 仍可能 fail");
        }
        return allOk;
    }

    /// <summary>
    /// 单文件下 expansion 元数据 ——
    /// 镜像 <see cref="DownloadSingleAsync"/> 的 .partial → final atomic rename
    /// pattern,但 stateless(不需要 instance state,HttpClient 由 caller 注入)。
    ///
    /// **fallback chain**:1) Settings 配的 HF mirror(如果配置) → 2) HF 官方 ——
    /// 镜像的 repo 不一定全(尤其小元数据文件可能被 sync 漏),404 时 retry 官方。
    /// 用户 dev build 实测 hf-mirror.com 缺 <c>fooocus_expansion</c> 6 个元数据
    /// 文件(只 mirror 了 351MB pytorch_model.bin);fallback 后从 huggingface.co 拿到。
    /// </summary>
    private static async Task<bool> DownloadExpansionMetadataSingleAsync(
        HttpClient http,
        string targetDir,
        string fileName,
        Settings? settings,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        var finalPath = Path.Combine(targetDir, fileName);
        var partialPath = finalPath + ".partial";

        var rawUrl = $"{FooocusExpansionMetadataConstants.ExpansionBaseUrl}/{fileName}";
        var mirrorUrl = ResolveMirror(rawUrl, settings);
        var useMirror = !string.Equals(mirrorUrl, rawUrl, StringComparison.OrdinalIgnoreCase);

        // Attempt 1: mirror(如果配了)
        if (await TryDownloadAsync(http, mirrorUrl, partialPath, logProgress, ct))
        {
            CommitPartialToFinal(partialPath, finalPath);
            logProgress?.Report($"[fooocus-expansion-meta] ✓ {fileName}");
            return true;
        }

        // Attempt 2: HF official(仅在配了 mirror 且 mirror 失败时 fallback;mirror 未配
        // 就直接 retry raw URL,避免重复)
        if (useMirror)
        {
            logProgress?.Report($"[fooocus-expansion-meta] ⚠ mirror 失败,fallback 到 HF 官方:{rawUrl}");
            if (await TryDownloadAsync(http, rawUrl, partialPath, logProgress, ct))
            {
                CommitPartialToFinal(partialPath, finalPath);
                logProgress?.Report($"[fooocus-expansion-meta] ✓ {fileName} (官方源)");
                return true;
            }
        }

        // 全失败
        try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
        logProgress?.Report($"[fooocus-expansion-meta] ✗ {fileName}:mirror + 官方 都 fail");
        return false;
    }

    /// <summary>
    /// 单 URL 下载尝试 —— 返 true = 成功写到 partialPath;返 false = 失败(404/网络/异常)。
    /// 失败时不删 partialPath(留给 caller 决定)。
    /// </summary>
    private static async Task<bool> TryDownloadAsync(
        HttpClient http,
        string url,
        string partialPath,
        IProgress<string>? logProgress,
        CancellationToken ct)
    {
        logProgress?.Report($"[fooocus-expansion-meta] ↓ {url}");
        try
        {
            using var resp = await http.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(partialPath,
                FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[DownloadBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            fileStream.Flush();
            fileStream.Close();
            return true;
        }
        catch
        {
            // swallow — caller decides retry vs give up
            return false;
        }
    }

    /// <summary>
    /// Atomic .partial → final rename(跟 <see cref="DownloadSingleAsync"/> line 307 一致)。
    /// </summary>
    private static void CommitPartialToFinal(string partialPath, string finalPath)
    {
        File.Move(partialPath, finalPath, overwrite: true);
    }

    private void WriteMarker(Environment env)
    {
        var markerPath = Path.Combine(env.RootPath, FooocusDefaultModelsConstants.MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception ex)
        {
            _logger?.Warn("fooocus-launcher-models", $"env='{env.Name}' marker 写失败:{ex.Message}");
        }
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
