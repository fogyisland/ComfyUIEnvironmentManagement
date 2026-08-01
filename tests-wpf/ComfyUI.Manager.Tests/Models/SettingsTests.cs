using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsTests
{
    [Fact]
    public void DefaultPythonVersion_DefaultsTo310()
    {
        var s = new Settings();
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_RoundTripsViaJson()
    {
        var s = new Settings { DefaultPythonVersion = "3.11" };
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"default_python_version\":\"3.11\"", json);
        var restored = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(restored);
        Assert.Equal("3.11", restored!.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_DefaultsWhenJsonMissing()
    {
        // 旧 settings.json 没有 default_python_version 字段 → 反序列化后仍是 "3.10"
        var oldJson = "{\"template_python_dir\":\"python\",\"template_comfyui_dir\":\"ComfyUI\"}";
        var restored = JsonSerializer.Deserialize<Settings>(oldJson);
        Assert.NotNull(restored);
        Assert.Equal("3.10", restored!.DefaultPythonVersion);
    }
}
