using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class SettingsHuggingFaceTests
{
    [Fact]
    public void ModelSourceHuggingFaceEnabled_DefaultsToFalse()
    {
        var s = new Settings();
        Assert.False(s.ModelSourceHuggingFaceEnabled);
    }

    [Fact]
    public void ModelSourceHuggingFaceUseMirror_DefaultsToTrue()
    {
        var s = new Settings();
        Assert.True(s.ModelSourceHuggingFaceUseMirror);
    }

    [Fact]
    public void ModelSourceHuggingFaceMirrorUrl_DefaultsToHfMirror()
    {
        var s = new Settings();
        Assert.Equal("https://hf-mirror.com", s.ModelSourceHuggingFaceMirrorUrl);
    }

    [Fact]
    public void Settings_LoadFromV0_6_20_Json_MigratesNewFieldsAsDefaults()
    {
        // Old v0.6.20 settings.json (no v0.6.21 fields) → all new fields get defaults
        var v0620Json = "{\"models_directory\":\"models\",\"model_source_civitai_enabled\":true}";
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(v0620Json);
        Assert.NotNull(s);
        Assert.False(s!.ModelSourceHuggingFaceEnabled);
        Assert.Equal("", s.HuggingFaceApiToken);
        Assert.True(s.ModelSourceHuggingFaceUseMirror);
        Assert.Equal("https://hf-mirror.com", s.ModelSourceHuggingFaceMirrorUrl);
        Assert.False(s.ModelSourceCivitAiUseMirror);
        Assert.Equal("", s.ModelSourceCivitAiMirrorUrl);
    }
}