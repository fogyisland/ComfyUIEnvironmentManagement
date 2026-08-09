using System;
using System.Collections.Generic;
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

    public DashboardService(
        IEnvironmentRepository envRepo,
        INodeRepository nodeRepo,
        AppLogger logger,
        HttpClient http)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
        _logger = logger;
        _http = http;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var envTask = Task.Run(() => _envRepo.ListAll(), ct);
        var nodeTask = _nodeRepo.CountAllAsync(ct);
        var opsTask = Task.Run(ReadRecentOps, ct);
        var releaseTask = FetchLatestReleaseAsync(ct);

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
        return new DashboardSnapshot(
            counts,
            nodeCount,
            recentOps,
            release.Tag,
            release.Failed,
            DateTimeOffset.Now);
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
