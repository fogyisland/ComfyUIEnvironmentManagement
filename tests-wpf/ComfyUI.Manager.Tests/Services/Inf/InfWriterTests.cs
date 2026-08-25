using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services.Inf;
using Xunit;

namespace ComfyUI.Manager.Tests.Services.Inf;

public sealed class InfWriterTests : IDisposable
{
    private readonly string _tempDir;

    public InfWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "inf-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_ThenParse_RoundTripsAllEntries()
    {
        var entries = new Dictionary<string, string>
        {
            ["theme"] = "material_purple",
            ["language"] = "zh_CN",
            ["catalog_page_size"] = "20",
            ["http_proxy_mode"] = "InheritSystem",
        };
        var text = InfWriter.ToText(entries);

        var parsed = InfParser.Parse(text);
        Assert.Equal(4, parsed.Count);
        Assert.Equal("material_purple", parsed["theme"]);
        Assert.Equal("zh_CN", parsed["language"]);
        Assert.Equal("20", parsed["catalog_page_size"]);
        Assert.Equal("InheritSystem", parsed["http_proxy_mode"]);
    }

    [Fact]
    public void Write_HeaderComment_AppearsInOutput()
    {
        var entries = new Dictionary<string, string> { ["theme"] = "dark" };
        var text = InfWriter.ToText(
            entries,
            new[] { "settings.inf — user config", "Located at config/settings.inf" });

        Assert.Contains("# settings.inf — user config", text);
        Assert.Contains("# Located at config/settings.inf", text);
        Assert.Contains("theme = dark", text);
    }

    [Fact]
    public void Write_JsonEncodedValue_WrittenAsRawString()
    {
        var json = "[{\"name\":\"foo\",\"enabled\":true}]";
        var entries = new Dictionary<string, string> { ["query_sources"] = json };
        var text = InfWriter.ToText(entries);

        var parsed = InfParser.Parse(text);
        Assert.Equal(json, parsed["query_sources"]);
    }

    [Fact]
    public void Write_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "deep", "nested", "config.inf");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)));

        InfWriter.Write(nested, new Dictionary<string, string> { ["k"] = "v" });

        Assert.True(File.Exists(nested));
        Assert.Equal("v", InfParser.ParseFile(nested)["k"]);
    }

    [Fact]
    public void Write_SortedByKey_StableOutput()
    {
        var entries = new Dictionary<string, string>
        {
            ["zebra"] = "z",
            ["alpha"] = "a",
            ["middle"] = "m",
        };
        var text = InfWriter.ToText(entries);

        // Windows StringBuilder.AppendLine 使用 \r\n;split 用 Environment.NewLine。
        var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        var firstEntryIdx = Array.IndexOf(lines, lines.First(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l)));
        Assert.Equal("alpha = a", lines[firstEntryIdx]);
        Assert.Equal("middle = m", lines[firstEntryIdx + 1]);
        Assert.Equal("zebra = z", lines[firstEntryIdx + 2]);
    }

    [Fact]
    public void Write_NoHeader_JustEntries()
    {
        var entries = new Dictionary<string, string> { ["theme"] = "dark" };
        var text = InfWriter.ToText(entries);
        Assert.DoesNotContain("#", text);
        Assert.Contains("theme = dark", text);
    }
}