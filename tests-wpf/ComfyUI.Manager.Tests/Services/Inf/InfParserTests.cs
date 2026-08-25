using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services.Inf;
using Xunit;

namespace ComfyUI.Manager.Tests.Services.Inf;

public sealed class InfParserTests : System.IDisposable
{
    private readonly string _tempDir;

    public InfParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "inf-parser-tests-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmptyDict()
    {
        Assert.Empty(InfParser.Parse(null));
        Assert.Empty(InfParser.Parse(""));
        Assert.Empty(InfParser.Parse("   \n  \n  "));
    }

    [Fact]
    public void Parse_SimpleKeyValue_ReturnsDict()
    {
        var text = "theme = material_purple\nlanguage = zh_CN\n";
        var result = InfParser.Parse(text);
        Assert.Equal("material_purple", result["theme"]);
        Assert.Equal("zh_CN", result["language"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Parse_BlankLinesAndComments_Skipped()
    {
        var text = """
            # this is a comment
            theme = dark

            # another comment

            language = zh_CN
            """;
        var result = InfParser.Parse(text);
        Assert.Equal(2, result.Count);
        Assert.Equal("dark", result["theme"]);
        Assert.Equal("zh_CN", result["language"]);
    }

    [Fact]
    public void Parse_ColonOrEqualsSeparator_BothAccepted()
    {
        var text = "a = 1\nb : 2\nc=3\nd:4\n";
        var result = InfParser.Parse(text);
        Assert.Equal("1", result["a"]);
        Assert.Equal("2", result["b"]);
        Assert.Equal("3", result["c"]);
        Assert.Equal("4", result["d"]);
    }

    [Fact]
    public void Parse_KeyCaseInsensitive_NormalizesToLowercase()
    {
        var text = "Theme = dark\nLANGUAGE = zh_CN\nThemeMode = dark\n";
        var result = InfParser.Parse(text);
        Assert.Equal("dark", result["theme"]);
        Assert.Equal("zh_CN", result["language"]);
        Assert.Equal("dark", result["thememode"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Parse_JsonEncodedValue_PreservedAsRawString()
    {
        var jsonValue = "[{\"name\":\"foo\",\"enabled\":true}]";
        var text = $"query_sources = {jsonValue}\n";
        var result = InfParser.Parse(text);
        Assert.Equal(jsonValue, result["query_sources"]);
    }

    [Fact]
    public void Parse_UnknownLine_SkippedSilently()
    {
        var text = "no_separator_here\ntheme = dark\n   \njust_garbage\n";
        var result = InfParser.Parse(text);
        Assert.Single(result);
        Assert.Equal("dark", result["theme"]);
    }

    [Fact]
    public void Parse_DuplicateKey_LastWins()
    {
        var text = "theme = light\ntheme = dark\n";
        var result = InfParser.Parse(text);
        Assert.Single(result);
        Assert.Equal("dark", result["theme"]);
    }

    [Fact]
    public void Parse_EmptyValue_Allowed()
    {
        var text = "civitai_api_token =\ngithub_token =\n";
        var result = InfParser.Parse(text);
        Assert.Equal("", result["civitai_api_token"]);
        Assert.Equal("", result["github_token"]);
    }

    [Fact]
    public void Parse_TrailingWhitespace_Trimmed()
    {
        var text = "theme    =   dark    \n";
        var result = InfParser.Parse(text);
        Assert.Equal("dark", result["theme"]);
    }

    [Fact]
    public void ParseAndCollectWarnings_UnknownLine_ReportsWarning()
    {
        var text = "no_separator\ntheme = dark\n";
        var result = InfParser.ParseAndCollectWarnings(text, out _, out var warnings);
        Assert.Single(result);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("no '=' or ':' separator"));
    }

    [Fact]
    public void ParseFile_MissingFile_ThrowsFileNotFoundException()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.inf");
        Assert.Throws<FileNotFoundException>(() => InfParser.ParseFile(missing));
    }

    [Fact]
    public void ParseFile_ExistingFile_ReadsAndParses()
    {
        var path = Path.Combine(_tempDir, "ok.inf");
        File.WriteAllText(path, "theme = dark\nlanguage = zh_CN\n");
        var result = InfParser.ParseFile(path);
        Assert.Equal("dark", result["theme"]);
        Assert.Equal("zh_CN", result["language"]);
    }
}