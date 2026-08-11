using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyUI.Manager.Models;

/// <summary>
/// Dashboard 一屏聚合数据。
///
/// v0.6.11+ dashboard/splash polish:扩展 changelog + releases + 下载路径字段。
///
/// **为什么新字段是 init-only 属性而不是 positional 参数**:positional record 参数
/// 的默认值必须是编译期常量,<c>IReadOnlyList&lt;T&gt;</c> 给不了(<c>Array.Empty&lt;T&gt;()</c>
/// 不是常量)。写成 body 里的 init 属性既能给非常量默认值,又让既有 6-arg 构造点
/// (DashboardService + 4 处测试)不用改,同时支持 <c>snapshot with { Releases = ... }</c>。
/// </summary>
public sealed record DashboardSnapshot(
    EnvironmentCounts EnvironmentCounts,
    long NodeCount,
    IReadOnlyList<RecentOperation> RecentOperations,
    string? LatestRelease,
    bool GitHubFailed,
    DateTimeOffset SnapshotAt)
{
    /// <summary>折叠态最多显示几条 changelog(展开后显示全部)。</summary>
    public const int VisibleChangelogLimit = 5;

    /// <summary>GitHub release 页面 URL — 「下载地址」区块的「浏览器打开」按钮用。</summary>
    public const string DefaultReleaseUrl =
        "https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases/latest";

    public int TotalEnvironments =>
        EnvironmentCounts.Running + EnvironmentCounts.Stopped + EnvironmentCounts.Undeployed;

    public bool HasGitHubInfo => LatestRelease is not null;

    /// <summary>GitHub Releases API 全 list(24h cache)。网络失败时为空 list,不为 null。</summary>
    public IReadOnlyList<GitHubRelease> Releases { get; init; } = Array.Empty<GitHubRelease>();

    /// <summary>
    /// CHANGELOG.md 解析结果。DashboardService 保证非空 —— 解析出 0 条时回退到
    /// <see cref="Services.ChangelogParser.HardcodedFallback"/>(见 DashboardService 注释)。
    /// </summary>
    public IReadOnlyList<ChangelogEntry> Changelog { get; init; } = Array.Empty<ChangelogEntry>();

    /// <summary>
    /// 仓库 star 数。当前恒为 null —— GitHub Releases API 不返回 star,
    /// 需要额外打 /repos/{owner}/{repo}。UI 用 TargetNullValue='—' 兜底。
    /// </summary>
    public int? GitHubStars { get; init; }

    /// <summary>已发布 release 条数(= <see cref="Releases"/>.Count,fetch 失败时 null)。</summary>
    public int? GitHubReleaseCount { get; init; }

    /// <summary>本地 staging 可执行文件绝对路径(「复制路径」/「打开文件夹」按钮用)。</summary>
    public string StagingPath { get; init; } = string.Empty;

    public string ReleaseUrl { get; init; } = DefaultReleaseUrl;

    /// <summary>GitHub releases 最后一次成功同步时间(UTC),从未成功则 null。</summary>
    public DateTime? LastChangelogSync { get; init; }

    public bool IsChangelogExpanded { get; init; }

    public IReadOnlyList<ChangelogEntry> VisibleChangelog =>
        IsChangelogExpanded ? Changelog : Changelog.Take(VisibleChangelogLimit).ToList();
}

public sealed record EnvironmentCounts(int Running, int Stopped, int Undeployed);

/// <summary>从 AppLogger 日志行解析出的单条最近操作。</summary>
public sealed record RecentOperation(
    DateTimeOffset ParsedTime,
    string Subsystem,
    string Message);
