using System;
using System.Linq;
using ComfyUI.Manager.Search;
using Xunit;

namespace ComfyUI.Manager.Tests.Search;

/// <summary>
/// v0.6.9 T6 测试契约 — 10 测试覆盖 5 档评分 + 边界 + tie-break + 中英混排。
/// </summary>
public sealed class SearchIndexTests
{
    // -- helpers ------------------------------------------------------------

    /// <summary>
    /// 给 (Kind, Name) 元组列表灌成 SearchIndex。
    /// NormalizedTokens 用 SearchIndex 内部同一套 TokenizeRaw —
    /// 关键:测试跟生产用同一规则,避免切词差异导致 false negative。
    /// </summary>
    private static SearchIndex IndexOf(params (string Kind, string Name)[] entries)
    {
        var idx = new SearchIndex();
        foreach (var (kind, name) in entries)
        {
            var k = Enum.Parse<TargetKind>(kind);
            idx.Add(new SearchEntry
            {
                Id = Guid.NewGuid().ToString(),
                Kind = k,
                DisplayName = name,
                NormalizedTokens = SearchIndex.TokenizeRaw(name),
                Target = new SearchTarget(k, DisplayName: name),
            });
        }
        return idx;
    }

    // -- 1. 空 query --------------------------------------------------------

    [Fact]
    public void Query_Empty_ReturnsEmpty()
    {
        var idx = IndexOf(("Environment", "foo"));
        Assert.Empty(idx.Query(""));
        Assert.Empty(idx.Query("   "));
        Assert.Empty(idx.Query("__--  "));
    }

    // -- 2. exact -----------------------------------------------------------

    [Fact]
    public void Query_ExactMatch_Scores100()
    {
        var idx = IndexOf(("Environment", "test-env"));
        var r = idx.Query("test-env").Single();
        Assert.Equal(100, r.Score);
    }

    [Fact]
    public void Query_ExactMatch_IgnoresCase()
    {
        // exact match 在 normalize 后大小写无关
        var idx = IndexOf(("Environment", "Production"));
        var r = idx.Query("PRODUCTION").Single();
        Assert.Equal(100, r.Score);
    }

    // -- 3. token prefix(80) ----------------------------------------------

    [Fact]
    public void Query_PrefixMatch_Scores80()
    {
        var idx = IndexOf(("Environment", "production"));
        var r = idx.Query("prod").Single();
        Assert.Equal(80, r.Score);
    }

    // -- 4. any-token prefix(60) -------------------------------------------

    [Fact]
    public void Query_TokenPrefix_Scores60()
    {
        // 多 token query,任一 name token 起于某 q token
        // name="test env"  → tokens=["test","env"]
        // query="env prod" → q tokens=["env","prod"]
        // "env" 是 name 的 token 之一,且 "env" 是 q 的 token 之一,name 有一 token 起于 q token → 60
        var idx = IndexOf(("Environment", "test env"));
        var r = idx.Query("env prod").Single();
        Assert.Equal(60, r.Score);
    }

    // -- 5. substring(40) --------------------------------------------------

    [Fact]
    public void Query_Substring_Scores40()
    {
        var idx = IndexOf(("Environment", "production"));
        var r = idx.Query("duct").Single();
        Assert.Equal(40, r.Score);
    }

    // -- 6. subsequence(20) ------------------------------------------------

    [Fact]
    public void Query_Subsequence_Scores20()
    {
        // "pdt" 不 substring in "production" 但 char-by-char 有序:p..d..t
        var idx = IndexOf(("Environment", "production"));
        var r = idx.Query("pdt").Single();
        Assert.Equal(20, r.Score);
    }

    // -- 7. 中英混排 --------------------------------------------------------

    [Fact]
    public void Query_ChineseText_Matches()
    {
        // "python 解释器" → tokens ["python", "解释器"]
        // "解释器" 整 token 等于 name 第二个 token → 80 分(token exact prefix)
        var idx = IndexOf(("Environment", "python 解释器"));
        var r = idx.Query("解释器").Single();
        Assert.True(r.Score >= 80, $"expected >=80, got {r.Score}");
    }

    [Fact]
    public void Query_ChineseText_AsciiBoundary()
    {
        // 关键测试:中英混排 "python解释器" 不带空格,ASCII↔CJK 边界分隔必须工作
        var idx = IndexOf(("Environment", "python解释器"));
        var r = idx.Query("python").Single();
        Assert.Equal(80, r.Score); // token prefix
    }

    [Fact]
    public void Query_Normalize_UnderscoreAndHyphen()
    {
        // "python_interpreter" → normalize 后 ["python", "interpreter"]
        var idx = IndexOf(("Environment", "python_interpreter"));
        var r = idx.Query("interp").Single();
        Assert.True(r.Score >= 40, $"expected >=40 (substring or prefix), got {r.Score}");
    }

    // -- 8. case insensitivity --------------------------------------------

    [Fact]
    public void Query_CaseInsensitive_Normalizes()
    {
        var idx = IndexOf(("Environment", "Production"));
        var r = idx.Query("PRODUCTION").Single();
        Assert.Equal(100, r.Score);
    }

    // -- 9. maxResults limit ----------------------------------------------

    [Fact]
    public void Query_RespectsMaxLimit()
    {
        // 30 entries all prefix-match "prod",limit=5,只返 5
        var entries = Enumerable.Range(0, 30)
            .Select(i => ("Environment", $"prod-{i:00}"))
            .ToArray();
        var idx = IndexOf(entries);
        var results = idx.Query("prod", maxResults: 5);
        Assert.Equal(5, results.Count);
    }

    // -- 10. tie-break -----------------------------------------------------

    [Fact]
    public void Query_TieBreak_KindPriorityAndShortText()
    {
        // 三条 "test",kind 分别是 Command/SettingsSection/Environment,score 相同 → Command 优先
        var idx = new SearchIndex();
        idx.Add(new SearchEntry
        {
            Id = "e",
            Kind = TargetKind.Environment,
            DisplayName = "test",
            NormalizedTokens = new[] { "test" },
            Target = new SearchTarget(TargetKind.Environment, DisplayName: "test"),
        });
        idx.Add(new SearchEntry
        {
            Id = "c",
            Kind = TargetKind.Command,
            DisplayName = "test",
            NormalizedTokens = new[] { "test" },
            Target = new SearchTarget(TargetKind.Command, CommandName: "Test", DisplayName: "test"),
        });
        idx.Add(new SearchEntry
        {
            Id = "s",
            Kind = TargetKind.SettingsSection,
            DisplayName = "test",
            NormalizedTokens = new[] { "test" },
            Target = new SearchTarget(TargetKind.SettingsSection, SectionKey: "test", DisplayName: "test"),
        });
        var results = idx.Query("test");
        Assert.Equal(3, results.Count);
        Assert.Equal("c", results[0].Entry.Id);  // Command priority 1
        Assert.Equal("s", results[1].Entry.Id);  // SettingsSection priority 2
        Assert.Equal("e", results[2].Entry.Id);  // Environment priority 3
    }

    [Fact]
    public void Query_TieBreak_SameKind_ShortTextFirst()
    {
        // 两个 Environment entry,score 相同,短 DisplayName 优先
        var idx = new SearchIndex();
        idx.Add(new SearchEntry
        {
            Id = "long",
            Kind = TargetKind.Environment,
            DisplayName = "test-long-name",
            NormalizedTokens = new[] { "test", "long", "name" },
            Target = new SearchTarget(TargetKind.Environment, DisplayName: "test-long-name"),
        });
        idx.Add(new SearchEntry
        {
            Id = "short",
            Kind = TargetKind.Environment,
            DisplayName = "test",
            NormalizedTokens = new[] { "test" },
            Target = new SearchTarget(TargetKind.Environment, DisplayName: "test"),
        });
        var results = idx.Query("test");
        // "test" exact match 100,"test-long-name" token prefix 80 → "test" 应该排第一
        Assert.Equal("short", results[0].Entry.Id);
        Assert.Equal("long", results[1].Entry.Id);
    }
}
