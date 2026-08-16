using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.15.11 picker meta enrichment:CatalogEntryPickerItem 新增 stat strip +
/// row details 用的所有 display helper。这里覆盖每个 helper 在 null / 空 / 正常值时的
/// 行为,确保 XAML 绑 null 时不会 NRE,UI 走 NullToVisibility 隐藏对应 chip。
/// </summary>
public class CatalogEntryPickerItemDisplayTests
{
    private static CatalogEntryPickerItem NewItem(CatalogEntry? entry = null)
        => new() { Entry = entry ?? new CatalogEntry { Package = "test-pkg" } };

    // ──────────────── stat strip helpers ────────────────

    [Fact]
    public void StarsDisplay_NullWhenZero()
    {
        var item = NewItem();
        Assert.Null(item.StarsDisplay);
    }

    [Fact]
    public void StarsDisplay_FormatsK_M_B()
    {
        Assert.Equal("★ 567", NewItem(new CatalogEntry { Stars = 567 }).StarsDisplay);
        Assert.Equal("★ 1.5K", NewItem(new CatalogEntry { Stars = 1500 }).StarsDisplay);  // <10K → decimal
        Assert.Equal("★ 10.0K", NewItem(new CatalogEntry { Stars = 9999 }).StarsDisplay);  // F1 向上取整
        Assert.Equal("★ 12K", NewItem(new CatalogEntry { Stars = 12345 }).StarsDisplay);   // ≥10K → int
        Assert.Equal("★ 1.5M", NewItem(new CatalogEntry { Stars = 1500000 }).StarsDisplay);  // <10M → decimal
        Assert.Equal("★ 12M", NewItem(new CatalogEntry { Stars = 12345678 }).StarsDisplay);  // ≥10M → int
        Assert.Equal("★ 1.5B", NewItem(new CatalogEntry { Stars = 1500000000 }).StarsDisplay);
    }

    [Fact]
    public void DownloadsDisplay_NullWhenZero_FormatsK_M()
    {
        Assert.Null(NewItem().DownloadsDisplay);
        Assert.Equal("↓ 56K", NewItem(new CatalogEntry { Downloads = 56789 }).DownloadsDisplay);  // ≥10K → int
        Assert.Equal("↓ 1.5M", NewItem(new CatalogEntry { Downloads = 1500000 }).DownloadsDisplay);
        Assert.Equal("↓ 5.7K", NewItem(new CatalogEntry { Downloads = 5678 }).DownloadsDisplay);  // <10K → decimal
    }

    [Fact]
    public void LicenseDisplay_NullWhenEmpty_TrimmedWhenPresent()
    {
        Assert.Null(NewItem(new CatalogEntry { License = null }).LicenseDisplay);
        Assert.Null(NewItem(new CatalogEntry { License = "" }).LicenseDisplay);
        Assert.Null(NewItem(new CatalogEntry { License = "   " }).LicenseDisplay);
        Assert.Equal("MIT", NewItem(new CatalogEntry { License = "  MIT  " }).LicenseDisplay);
        Assert.Equal("Apache-2.0", NewItem(new CatalogEntry { License = "Apache-2.0" }).LicenseDisplay);
    }

    [Fact]
    public void LanguageDisplay_NullWhenEmpty()
    {
        Assert.Null(NewItem(new CatalogEntry { Language = null }).LanguageDisplay);
        Assert.Equal("Python", NewItem(new CatalogEntry { Language = "Python" }).LanguageDisplay);
    }

    [Fact]
    public void PythonCompatDisplay_JoinsList_NullWhenEmpty()
    {
        Assert.Null(NewItem().PythonCompatDisplay);
        Assert.Null(NewItem(new CatalogEntry { PythonCompat = new List<string>() }).PythonCompatDisplay);
        Assert.Equal("py 3.10", NewItem(new CatalogEntry { PythonCompat = new List<string> { "3.10" } }).PythonCompatDisplay);
        Assert.Equal("py 3.10+3.11", NewItem(new CatalogEntry { PythonCompat = new List<string> { "3.10", "3.11" } }).PythonCompatDisplay);
        // >3 项 → 截前 3 + "+(剩余数)" 计数后缀
        Assert.Equal("py 3.10+3.11+3.12+3", NewItem(new CatalogEntry
        {
            PythonCompat = new List<string> { "3.10", "3.11", "3.12", "3.13", "3.14", "3.9" }
        }).PythonCompatDisplay);
    }

    [Theory]
    [InlineData("windows", "🪟")]
    [InlineData("Windows", "🪟")]
    [InlineData("win", "🪟")]
    [InlineData("macos", "🍎")]
    [InlineData("mac", "🍎")]
    [InlineData("darwin", "🍎")]
    [InlineData("linux", "🐧")]
    [InlineData("ubuntu", "🐧")]
    public void OsCompatIcons_MapsToEmoji(string osInput, string expectedIcon)
    {
        var item = NewItem(new CatalogEntry { OsCompat = new List<string> { osInput } });
        Assert.Equal(expectedIcon, item.OsCompatIcons);
    }

    [Fact]
    public void OsCompatIcons_MultiOs_AllEmojis()
    {
        var item = NewItem(new CatalogEntry
        {
            OsCompat = new List<string> { "windows", "macos", "linux" }
        });
        Assert.Equal("🪟 🍎 🐧", item.OsCompatIcons);
    }

    [Fact]
    public void OsCompatIcons_NullOnEmptyOrUnknown()
    {
        Assert.Null(NewItem().OsCompatIcons);
        Assert.Null(NewItem(new CatalogEntry { OsCompat = new List<string> { "freebsd" } }).OsCompatIcons);
    }

    // ──────────────── row details helpers ────────────────

    [Fact]
    public void TagsDisplay_JoinsComma_TruncatesAt5()
    {
        Assert.Null(NewItem().TagsDisplay);
        Assert.Equal("a, b", NewItem(new CatalogEntry { Tags = new List<string> { "a", "b" } }).TagsDisplay);
        Assert.Equal("a, b, c, d, e +3", NewItem(new CatalogEntry
        {
            Tags = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" }
        }).TagsDisplay);
    }

    [Fact]
    public void PipRequirementsDisplay_NullWhenEmpty_PassThroughOtherwise()
    {
        Assert.Null(NewItem().PipRequirementsDisplay);
        var reqs = new[]
        {
            new PipRequirement("torch", ">=2.0"),
            new PipRequirement("numpy", null),
        };
        var item = NewItem(new CatalogEntry { PipRequirements = reqs });
        Assert.NotNull(item.PipRequirementsDisplay);
        Assert.Equal(2, item.PipRequirementsDisplay!.Count);
        Assert.Equal("torch", item.PipRequirementsDisplay[0].Name);
        Assert.Equal(">=2.0", item.PipRequirementsDisplay[0].Specifier);
    }

    [Fact]
    public void StatChips_EmptyWhenAllMetaFieldsMissing()
    {
        var item = NewItem();
        Assert.Empty(item.StatChips);
    }

    [Fact]
    public void StatChips_PresentForEachNonEmptyField()
    {
        var item = NewItem(new CatalogEntry
        {
            License = "MIT",
            Language = "Python",
            Stars = 100,
            Downloads = 500,
            PythonCompat = new List<string> { "3.10" },
            OsCompat = new List<string> { "linux" },
            Deprecated = true,
        });
        var chips = item.StatChips.Select(c => c.Display).ToList();
        // 7 chip:License / Language / Stars / Downloads / Python compat / OS compat / DEPRECATED
        Assert.Equal(7, chips.Count);
        Assert.Contains("MIT", chips);
        Assert.Contains("Python", chips);
        Assert.Contains("★ 100", chips);
        Assert.Contains("↓ 500", chips);
        Assert.Contains("py 3.10", chips);
        Assert.Contains("🐧", chips);
        Assert.Contains("DEPRECATED", chips);
    }

    [Fact]
    public void StatChips_LazyBuilt_AfterEntryAssigned()
    {
        // 测试 ctor 不需要 Entry(用 object initializer 后才填),StatChips lazy 构造
        // 不能在 ctor 跑(那时 Entry 还是 default null! 会 NRE)。
        var item = new CatalogEntryPickerItem();  // 没 Entry
        // 第一次访问 StatChips 会 NRE 因为 Entry 是 null
        Assert.Throws<System.NullReferenceException>(() => item.StatChips.ToList());
    }
}
