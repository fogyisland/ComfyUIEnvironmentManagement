using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ComfyUI.Manager.Tools.Ts2Resx;
using Xunit;

namespace ComfyUI.Manager.Tools.Ts2Resx.Tests;

public class ResxEmitterTests
{
    [Fact]
    public void Emit_WritesValidResxXml()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"test_{Guid.NewGuid()}.resx");
        try
        {
            ResxEmitter.Emit(path, null, new[]
            {
                ("CatalogPage_刷新", "Refresh"),
                ("EnvPage_环境", "Environment"),
            });
            var doc = XDocument.Load(path);
            var datas = doc.Root!.Elements("data").ToList();
            Assert.Equal(2, datas.Count);
            Assert.Contains(datas, d => d.Attribute("name")?.Value == "CatalogPage_刷新"
                && d.Element("value")?.Value == "Refresh");
            Assert.Contains(datas, d => d.Attribute("name")?.Value == "EnvPage_环境"
                && d.Element("value")?.Value == "Environment");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Emit_PreservesUnicodeKeys()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"test_{Guid.NewGuid()}.resx");
        try
        {
            ResxEmitter.Emit(path, null, new[]
            {
                ("中文_key", "值"),
            });
            var doc = XDocument.Load(path);
            var data = doc.Root!.Element("data");
            Assert.Equal("中文_key", data?.Attribute("name")?.Value);
            Assert.Equal("值", data?.Element("value")?.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}