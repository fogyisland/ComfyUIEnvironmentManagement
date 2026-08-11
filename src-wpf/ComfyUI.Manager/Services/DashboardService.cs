using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

public interface IDashboardService
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// v0.6.11+ T3 fix:把 service 内部的 logger 暴露给 VM ——
    /// dashboard 上的 clipboard / explorer / BrowserLauncher shell 副作用失败走
    /// 这个 logger 落 Logs/(spec §G8),不重复在 VM 里 inject 一份 AppLogger。
    /// Default null → 既有 stub 不需要改(只有 DashboardService 真实实现会覆写)。
    /// </summary>
    AppLogger? Logger => null;
}

public sealed class DashboardService : IDashboardService
{
    private static readonly Regex LogLineRegex = new(
        @"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\]\s+\[(\w+)\s*\]\s+\[([^\]]+)\]\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEnvironmentRepository _envRepo;
    private readonly INodeRepository _nodeRepo;
    private readonly AppLogger _logger;
    private readonly HttpClient _http;
    private readonly GitHubReleaseService? _releaseService;
    private readonly ChangelogParser _changelogParser;
    private readonly string _stagingPath;
    private readonly string _releaseUrl;
    private readonly string _changelogPath;

    /// <summary>v0.6.11+ T3 fix:暴露 logger 给 VM 用(详见 <see cref="IDashboardService.Logger"/>)。</summary>
    public AppLogger? Logger => _logger;

    /// <param name="releaseService">
    /// null → <see cref="DashboardSnapshot.Releases"/> 保持空 list(既有 4-arg 构造点行为不变)。
    /// </param>
    /// <param name="changelogParser">null → 内部 new 一个(parser 无状态)。</param>
    /// <param name="stagingPath">null → <c>AppContext.BaseDirectory/ComfyUI.Manager.exe</c>(G7)。</param>
    /// <param name="changelogPath">null → <c>AppContext.BaseDirectory/CHANGELOG.md</c>(G7);测试指向 fixture。</param>
    public DashboardService(
        IEnvironmentRepository envRepo,
        INodeRepository nodeRepo,
        AppLogger logger,
        HttpClient http,
        GitHubReleaseService? releaseService = null,
        ChangelogParser? changelogParser = null,
        string? stagingPath = null,
        string releaseUrl = DashboardSnapshot.DefaultReleaseUrl,
        string? changelogPath = null)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _logger = logger;
        _http = http;
        _releaseService = releaseService;
        _changelogParser = changelogParser ?? new ChangelogParser();
        _stagingPath = stagingPath ?? Path.Combine(AppContext.BaseDirectory, "ComfyUI.Manager.exe");
        _releaseUrl = releaseUrl;
        _changelogPath = changelogPath ?? Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var envTask = Task.Run(() => _envRepo.ListAll(), ct);
        var nodeTask = _nodeRepo.CountAllAsync(ct);
        var opsTask = Task.Run(ReadRecentOps, ct);
        var releaseTask = FetchLatestReleaseAsync(ct);
        // v0.6.11+ T3:两个新数据源跟原有 4 个并行跑(G7 —— 不串行叠加延迟)。
        var releasesTask = FetchReleaseListAsync(ct);
        var changelogTask = Task.Run(ReadChangelog, ct);

        EnvironmentCounts counts;
        long nodeCount;
        IReadOnlyList<RecentOperation> recentOps;
        (string? Tag, bool Failed) release;

        try
        {
            counts = ComputeCounts(await envTask.WaitAsync(ct));
            nodeCount = await nodeTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        try
        {
            recentOps = await opsTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            recentOps = Array.Empty<RecentOperation>();
        }

        release = await releaseTask.WaitAsync(ct);
        var releases = await releasesTask.WaitAsync(ct);
        var changelog = await changelogTask.WaitAsync(ct);

        return new DashboardSnapshot(
            counts,
            nodeCount,
            recentOps,
            release.Tag,
            release.Failed,
            DateTimeOffset.Now)
        {
            Releases = releases,
            Changelog = changelog,
            // GitHub Releases API 不返回 star 数 —— 需要另打 /repos/{owner}/{repo}。
            // 本轮不加这次请求(GitHub 未认证 60 req/h),UI 以 '—' 显示。
            GitHubStars = null,
            GitHubReleaseCount = releases.Count > 0 ? releases.Count : null,
            StagingPath = _stagingPath,
            ReleaseUrl = _releaseUrl,
            LastChangelogSync = _releaseService?.LastSyncUtc,
        };
    }

    /// <summary>
    /// GitHub releases 全 list(24h cache)。releaseService 未注入 → 空 list。
    /// GitHubReleaseService 内部已 catch 网络异常返回 last cached,这里只兜住
    /// cache 文件损坏抛出的 JsonException(它是刻意往上抛的)。
    /// </summary>
    private async Task<IReadOnlyList<GitHubRelease>> FetchReleaseListAsync(CancellationToken ct)
    {
        if (_releaseService is null) return Array.Empty<GitHubRelease>();
        try
        {
            return await _releaseService.FetchAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("dashboard", $"release list fetch failed: {ex.Message}");
            return Array.Empty<GitHubRelease>();
        }
    }

    /// <summary>
    /// 读 CHANGELOG.md 并解析。
    ///
    /// **回退链**(CF-T1-A):ChangelogParser.Parse 对空输入 / 没有 '## ' 段的输入
    /// 返回空 list(不是 fallback),所以「回退到 HardcodedFallback」这步必须由调用方做 ——
    /// 否则文件缺失时「最近更新」卡片直接空白。文件不存在 / 读失败同样走 fallback。
    /// </summary>
    private IReadOnlyList<ChangelogEntry> ReadChangelog()
    {
        string markdown;
        try
        {
            markdown = File.Exists(_changelogPath)
                ? File.ReadAllText(_changelogPath)
                : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warn("dashboard", $"CHANGELOG.md read failed ({_changelogPath}): {ex.Message}");
            return _changelogParser.HardcodedFallback;
        }

        IReadOnlyList<ChangelogEntry> entries;
        try
        {
            entries = _changelogParser.Parse(markdown);
        }
        catch (Exception ex)
        {
            _logger.Warn("dashboard", $"CHANGELOG.md parse failed: {ex.Message}");
            return _changelogParser.HardcodedFallback;
        }

        return entries.Count == 0 ? _changelogParser.HardcodedFallback : entries;
    }

    private static EnvironmentCounts ComputeCounts(IReadOnlyList<Environment> envs)
    {
        var running = 0;
        var stopped = 0;
        var undeployed = 0;
        foreach (var env in envs)
        {
            if (env.Status is "running" or "pending") running++;
            else if (env.Status == "stopped") stopped++;

            // BED deployment is orthogonal to process status, so this may overlap.
            if (env.BedStatus is null) undeployed++;
        }
        return new EnvironmentCounts(running, stopped, undeployed);
    }

    private IReadOnlyList<RecentOperation> ReadRecentOps()
    {
        try
        {
            var operations = new List<RecentOperation>();
            foreach (var line in _logger.ReadRecentLines(daysBack: 2, maxLines: 5))
            {
                var parsed = ParseRecentOp(line);
                if (parsed is not null) operations.Add(parsed);
            }
            return operations;
        }
        catch
        {
            return Array.Empty<RecentOperation>();
        }
    }

    private static RecentOperation? ParseRecentOp(string line)
    {
        var match = LogLineRegex.Match(line);
        if (!match.Success ||
            !TimeSpan.TryParseExact(match.Groups[1].Value, @"hh\:mm\:ss\.fff", null, out var time))
            return null;

        var today = DateTimeOffset.Now.Date;
        var parsedTime = new DateTimeOffset(today, DateTimeOffset.Now.Offset).Add(time);
        return new RecentOperation(parsedTime, match.Groups[3].Value, match.Groups[4].Value);
    }

    private async Task<(string? Tag, bool Failed)> FetchLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/fogyisland/ComfyUIEnvironmentManagement/releases/latest");
            request.Headers.UserAgent.ParseAdd("ComfyUI-Manager-WPF");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, true);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tag_name", out var tag)) return (null, true);
            var value = tag.GetString();
            return value is null ? (null, true) : (value, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, true);
        }
    }
}
