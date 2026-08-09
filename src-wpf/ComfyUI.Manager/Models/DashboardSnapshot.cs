using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>Dashboard 一屏聚合数据。</summary>
public sealed record DashboardSnapshot(
    EnvironmentCounts EnvironmentCounts,
    long NodeCount,
    IReadOnlyList<RecentOperation> RecentOperations,
    string? LatestRelease,
    bool GitHubFailed,
    DateTimeOffset SnapshotAt)
{
    public int TotalEnvironments =>
        EnvironmentCounts.Running + EnvironmentCounts.Stopped + EnvironmentCounts.Undeployed;

    public bool HasGitHubInfo => LatestRelease is not null;
}

public sealed record EnvironmentCounts(int Running, int Stopped, int Undeployed);

/// <summary>从 AppLogger 日志行解析出的单条最近操作。</summary>
public sealed record RecentOperation(
    DateTimeOffset ParsedTime,
    string Subsystem,
    string Message);
