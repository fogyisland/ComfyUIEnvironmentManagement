using System;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ChangelogParserTests
{
    [Fact]
    public void Parse_StandardMarkdown_ReturnsOrderedEntries()
    {
        var md = @"
# Changelog

## v0.6.11 (2026-08-11)
- env-list 4 按钮 → 2 toggle
- toolbar BED 按钮删除

## v0.6.10 (2026-08-10)
- env 两行按钮
- Chrome fallback";

        var p = new ChangelogParser();
        var entries = p.Parse(md);

        Assert.Equal(2, entries.Count);
        Assert.Equal("v0.6.11", entries[0].Version);
        Assert.Equal(new DateTime(2026, 8, 11), entries[0].Date);
        Assert.Equal(2, entries[0].BulletPoints.Count);
        Assert.Contains("env-list 4 按钮 → 2 toggle", entries[0].BulletPoints);
    }

    [Fact]
    public void Parse_NestedBullets_PreservesHierarchy()
    {
        var md = "## v0.6.9\n- top item\n  - sub item\n  - sub item 2\n- another top";
        var p = new ChangelogParser();
        var entries = p.Parse(md);
        Assert.Single(entries);
        Assert.Equal(3, entries[0].BulletPoints.Count); // 2 sub + 1 top-level after
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var p = new ChangelogParser();
        Assert.Empty(p.Parse(""));
        Assert.Empty(p.Parse("# Only title\n\nNo versions"));
    }

    [Fact]
    public void HardcodedFallback_NonEmpty()
    {
        var p = new ChangelogParser();
        Assert.NotEmpty(p.HardcodedFallback);
        Assert.True(p.HardcodedFallback.Count >= 3);
        // v0.6.11 必须出现(用户当前 SDD 落地的版本)
        Assert.Contains(p.HardcodedFallback, e => e.Version == "v0.6.11");
    }
}
