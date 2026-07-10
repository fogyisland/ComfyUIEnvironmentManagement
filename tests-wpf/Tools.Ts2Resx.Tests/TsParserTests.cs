using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Tools.Ts2Resx;
using Xunit;

namespace ComfyUI.Manager.Tools.Ts2Resx.Tests;

public class TsParserTests
{
    [Fact]
    public void Parse_ExtractsLocaleAndMessages()
    {
        var path = Path.Combine("Fixtures", "en_US_sample.ts");
        var ts = TsParser.Parse(path);
        Assert.Equal("en_US", ts.Locale);
        Assert.Equal(4, ts.Messages.Count);
    }

    [Fact]
    public void Parse_MarksVanishedMessages()
    {
        var path = Path.Combine("Fixtures", "en_US_sample.ts");
        var ts = TsParser.Parse(path);
        var vanished = ts.Messages.Single(m => m.Source == "已废弃");
        Assert.True(vanished.IsVanished);
    }

    [Fact]
    public void Parse_ExtractsExtraContext()
    {
        var path = Path.Combine("Fixtures", "en_US_sample.ts");
        var ts = TsParser.Parse(path);
        var withCtx = ts.Messages.Single(m => m.Source == "带上下文");
        Assert.Equal("button", withCtx.ExtraContext);
    }

    [Fact]
    public void Parse_HandlesMultipleContexts()
    {
        var path = Path.Combine("Fixtures", "en_US_sample.ts");
        var ts = TsParser.Parse(path);
        Assert.Contains(ts.Messages, m => m.Context == "EnvPage");
        Assert.Contains(ts.Messages, m => m.Context == "CatalogPage");
    }
}