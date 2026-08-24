using System.IO;
using System.Linq;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0 sidebar.inf 控制侧栏启用。解析器接受宽松格式:
/// key=value 或 key:value,大小写无关,前后空白忽略,空行/#-prefix 注释忽略,
/// 未知 key / 无法解析的行 → 跳过,生成警告。
/// </summary>
public class SidebarInfParserTests
{
    private const string DefaultKnownKeys =
        "Dashboard=1\nEnvironments=1\nCatalog=1\nLocalNodes=1\nWorkflows=1\nTemplates=1\nModels=1\nSettings=1\nBulkUpdate=1\nSystemStatus=1\n";

    [Fact]
    public void Parse_AllKeysPresent_ReturnsEnabledForAll()
    {
        var dict = SidebarInfParser.Parse(DefaultKnownKeys);
        // 10 个 MainSection 全部 enabled
        foreach (MainSection s in System.Enum.GetValues<MainSection>())
        {
            Assert.True(dict[s], $"{s} should be enabled when its key is 1");
        }
    }

    [Fact]
    public void Parse_ZeroValues_DisablesOnlyThoseSections()
    {
        var text = DefaultKnownKeys
            .Replace("Catalog=1", "Catalog=0")
            .Replace("Workflows=1", "Workflows=0")
            .Replace("Models=1", "Models=0");
        var dict = SidebarInfParser.Parse(text);
        Assert.False(dict[MainSection.Catalog]);
        Assert.False(dict[MainSection.Workflows]);
        Assert.False(dict[MainSection.Models]);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.BulkUpdate]);
    }

    [Fact]
    public void Parse_UnknownKey_SkippedAndWarned()
    {
        var text = "Dashboard=1\nUnknownSection=0\nEnvironments=1\n";
        var dict = SidebarInfParser.ParseAndCollectWarnings(text, out var warnings);
        Assert.Contains(warnings, w => w.Contains("UnknownSection"));
        Assert.True(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.Environments]);
    }

    [Fact]
    public void Parse_MalformedLine_Skipped()
    {
        // 坏行:无 key(=garbage) / 未知 value(Workflows=NOPE) — 跳过,不在 dict 里。
        // 空 value(Catalog=)按 0 处理 — ini 习惯写法。
        var text = "Dashboard=1\n=garbage\nCatalog=\nWorkflows=NOPE\nEnvironments=1\n";
        var dict = SidebarInfParser.Parse(text);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.Environments]);
        // 空 value 当 0
        Assert.True(dict.ContainsKey(MainSection.Catalog));
        Assert.False(dict[MainSection.Catalog]);
        // 未知 value → 跳过 → 不在 dict 里
        Assert.False(dict.ContainsKey(MainSection.Workflows));
    }

    [Fact]
    public void Parse_CaseInsensitiveKeys()
    {
        var text = "dashboard=0\nWORKFLOWS=1\ncatalog=0\n";
        var dict = SidebarInfParser.Parse(text);
        Assert.False(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.Workflows]);
        Assert.False(dict[MainSection.Catalog]);
    }

    [Fact]
    public void Parse_WhitespaceTolerant()
    {
        var text = "  Dashboard  =  1 \n\tEnvironments\t=\t1\n Catalog = 0 \n";
        var dict = SidebarInfParser.Parse(text);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.Environments]);
        Assert.False(dict[MainSection.Catalog]);
    }

    [Fact]
    public void Parse_ColonSeparator_AlsoAccepted()
    {
        var text = "Dashboard:1\nEnvironments:0\n";
        var dict = SidebarInfParser.Parse(text);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.False(dict[MainSection.Environments]);
    }

    [Fact]
    public void Parse_CommentsAndBlankLines_Ignored()
    {
        var text = "# this is a comment\n\nDashboard=1\n  # another comment\nEnvironments=1\n\n";
        var dict = SidebarInfParser.Parse(text);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.True(dict[MainSection.Environments]);
    }

    [Fact]
    public void Parse_EmptyStream_ReturnsEmptyDict()
    {
        var dict = SidebarInfParser.Parse("");
        Assert.Empty(dict);
    }

    [Fact]
    public void Parse_NullStream_ReturnsEmptyDict()
    {
        var dict = SidebarInfParser.Parse((string)null!);
        Assert.Empty(dict);
    }

    [Fact]
    public void Parse_TextReader_OverloadWorks()
    {
        using var sr = new StringReader("Dashboard=1\nCatalog=0\n");
        var dict = SidebarInfParser.Parse(sr);
        Assert.True(dict[MainSection.Dashboard]);
        Assert.False(dict[MainSection.Catalog]);
    }
}
