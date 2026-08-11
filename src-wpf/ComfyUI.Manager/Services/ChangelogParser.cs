using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11+ dashboard/splash polish:CHANGELOG.md 解析器。
/// 简单 regex 切 '## VERSION (DATE)' 段;每段下 '- xxx' 行作为 bullet points。
/// 嵌套 bullet:父项如果紧跟着更深缩进的子项,父项本身当作分组标题丢弃,只保留子项
/// (跟 spec 的 "2 sub + 1 top-level after" 期望一致)。
/// 输入为空 / 没有任何 '## ' 段时返回空 list;调用方自行决定是否回退到
/// <see cref="HardcodedFallback"/>(3 条 hardcoded entries)。
/// </summary>
public sealed class ChangelogParser
{
    // '## v0.6.11 (2026-08-11)' or '## v0.6.11'
    private static readonly Regex SectionHeader = new(
        @"^##\s+(?<version>\S+)(?:\s+\((?<date>\d{4}-\d{2}-\d{2})\))?",
        RegexOptions.Compiled);

    // '- xxx' or '  - xxx'(嵌套)行 → indent + xxx text
    private static readonly Regex BulletLine = new(
        @"^(?<indent>\s*)-\s+(?<text>.+)$",
        RegexOptions.Compiled);

    public IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<ChangelogEntry>();

        var entries = new List<ChangelogEntry>();

        string? version = null;
        DateTime? date = null;
        var bullets = new List<(int Indent, string Text)>();

        void Flush()
        {
            if (version is null) return;
            entries.Add(new ChangelogEntry(version, date, Flatten(bullets)));
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var m = SectionHeader.Match(line);
            if (m.Success)
            {
                Flush();
                version = m.Groups["version"].Value;
                date = m.Groups["date"].Success
                    && DateTime.TryParse(m.Groups["date"].Value, out var d)
                    ? d
                    : null;
                bullets = new List<(int, string)>();
                continue;
            }

            var bm = BulletLine.Match(line);
            if (bm.Success && version is not null)
            {
                bullets.Add((bm.Groups["indent"].Value.Length,
                    bm.Groups["text"].Value.Trim()));
            }
        }

        Flush();
        return entries;
    }

    /// <summary>
    /// 扁平化 bullet 列表:丢弃"紧跟更深缩进子项"的父项(它只是分组标题),其余按原顺序保留。
    /// </summary>
    private static IReadOnlyList<string> Flatten(List<(int Indent, string Text)> bullets)
    {
        var result = new List<string>(bullets.Count);
        for (var i = 0; i < bullets.Count; i++)
        {
            var hasDeeperChild = i + 1 < bullets.Count
                && bullets[i + 1].Indent > bullets[i].Indent;
            if (hasDeeperChild) continue;
            result.Add(bullets[i].Text);
        }
        return result;
    }

    /// <summary>
    /// 3 条 hardcoded fallback,CHANGELOG.md 缺失 / 解析失败时用。
    /// 每次大 release 手动更新。
    /// </summary>
    public IReadOnlyList<ChangelogEntry> HardcodedFallback { get; } = new[]
    {
        new ChangelogEntry("v0.6.11", new DateTime(2026, 8, 11), new[]
        {
            "env-list 4 按钮 → 2 toggle(Requirements + BED)",
            "toolbar 基础环境部署 按钮删除",
        }),
        new ChangelogEntry("v0.6.10", new DateTime(2026, 8, 10), new[]
        {
            "env 两行按钮布局",
            "Chrome browser fallback",
            "全局默认 Models 路径",
        }),
        new ChangelogEntry("v0.6.9", new DateTime(2026, 8, 9), new[]
        {
            "UI Modernization:双主题 + Dashboard + Spotlight + 动效",
            "MaterialTextBox + CatalogTile Setter 修复",
        }),
    };
}
