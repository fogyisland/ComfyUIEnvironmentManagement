using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

public sealed class NodelistBackgroundJob : IDisposable
{
    private readonly NodelistDownloader _downloader;
    private readonly NodelistIngestor _ingestor;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public NodelistBackgroundJob(
        NodelistDownloader downloader,
        NodelistIngestor ingestor,
        Settings settings,
        AppLogger? logger = null)
    {
        _downloader = downloader;
        _ingestor = ingestor;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 触发条件:用户原话"我说的字段是github token 设置了 或者 自定义查询网站和token设置了才会开启后台增量刷新"。
    /// - GitHub 分支:看 NodelistHostToken(GitHub PAT)是否非空
    /// - Custom 分支:看 NodelistCustomHostUrl + NodelistCustomToken 是否都非空
    /// 不满足时 RunOnceAsync 应直接 skip,后台线程不要打 error 也不要 throw。
    /// </summary>
    private bool ShouldRun()
    {
        if (_settings.NodelistHostKind == NodelistHostKind.GitHub)
            return !string.IsNullOrWhiteSpace(_settings.NodelistHostToken);
        return !string.IsNullOrWhiteSpace(_settings.NodelistCustomHostUrl)
            && !string.IsNullOrWhiteSpace(_settings.NodelistCustomToken);
    }

    public void Start(TimeSpan initialDelay, TimeSpan period)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(async () =>
        {
            _logger?.Info("NodelistBackgroundJob",
                $"启动 {initialDelay} 后跑首次,之后每 {period} 跑一次");
            try { await Task.Delay(initialDelay, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await RunOnceSafeAsync(token);
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(period, token); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;
                await RunOnceSafeAsync(token);
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task RunOnceSafeAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.Error("NodelistBackgroundJob", "异常(下次继续)", ex);
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        // 触发条件检查 — 用户原话"只有设置了字段才会后台更新 nodes"。
        // 没满足时直接静默 skip,不打 error 也不 throw,后台 loop 继续按周期跑(下次用户改设置后才生效)。
        if (!ShouldRun())
        {
            _logger?.Info("NodelistBackgroundJob", "trigger 未配置(NodelistHostToken 或 Custom URL+Token 缺失),跳过本次入库");
            return false;
        }
        var dir = _settings.NodelistDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            _logger?.Warn("NodelistBackgroundJob", "NodelistDirectory 未配置,跳过");
            return false;
        }
        var jsonPath = Path.Combine(dir, "nodelist", "custom-node-list.json");
        try
        {
            if (!File.Exists(jsonPath))
            {
                _logger?.Info("NodelistBackgroundJob", $"{jsonPath} 不存在,下载默认仓库");
                var dl = await _downloader.DownloadDefaultAsync(dir, ct);
                _logger?.Info("NodelistBackgroundJob", $"下载完成 {dl.SizeBytes} bytes");
            }
            // GitHub 分支走 api.github.com + NodelistHostToken,Custom 分支走用户填的 URL + NodelistCustomToken
            var host = _settings.NodelistHostKind == NodelistHostKind.GitHub
                ? "https://api.github.com"
                : _settings.NodelistCustomHostUrl;
            var token = _settings.NodelistHostKind == NodelistHostKind.GitHub
                ? _settings.NodelistHostToken
                : _settings.NodelistCustomToken;
            var result = await _ingestor.IngestAsync(
                jsonPath, host, token, forceFull: false, progress: null, ct: ct);
            _settings.NodelistLastIngestAt = DateTime.UtcNow.ToString("o");
            _logger?.Info("NodelistBackgroundJob",
                $"增量入库完成 — 扫 {result.EntriesScanned} 个 entry,新增 {result.EntriesNew},跳过 {result.EntriesSkipped},详情写入 {result.DetailsWritten},详情失败 {result.DetailsFailed},用时 {result.Elapsed.TotalSeconds:F1}s");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger?.Error("NodelistBackgroundJob", "入库失败", ex);
            return false;
        }
    }
}
