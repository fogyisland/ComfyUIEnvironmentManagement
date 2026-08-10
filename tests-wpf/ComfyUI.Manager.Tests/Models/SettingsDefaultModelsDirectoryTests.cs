using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsDefaultModelsDirectoryTests
{
    [Fact]
    public void DefaultModelsDirectory_DefaultsToEmptyString()
    {
        var s = new Settings();
        Assert.Equal("", s.DefaultModelsDirectory);
    }

    [Fact]
    public void DefaultModelsDirectory_RoundTripsViaJson()
    {
        var s = new Settings { DefaultModelsDirectory = "D:/models" };
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"default_models_directory\":\"D:/models\"", json);
        var restored = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(restored);
        Assert.Equal("D:/models", restored!.DefaultModelsDirectory);
    }

    [Fact]
    public void DefaultModelsDirectory_DefaultsWhenJsonMissing()
    {
        // 旧 settings.json 没有 default_models_directory 字段 → 反序列化后仍是 ""
        var oldJson = "{\"shared_models_directory\":\"D:/shared\"}";
        var restored = JsonSerializer.Deserialize<Settings>(oldJson);
        Assert.NotNull(restored);
        Assert.Equal("", restored!.DefaultModelsDirectory);
    }
}