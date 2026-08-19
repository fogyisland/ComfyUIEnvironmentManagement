using System.Collections.Generic;
using System.Net.Http;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceFactoryTests
{
    private static Settings MakeSettings(
        bool civitai = true, bool civitaiMirror = false, string civitaiMirrorUrl = "",
        bool hf = false, string hfToken = "", bool hfMirror = true, string hfMirrorUrl = "https://hf-mirror.com")
        => new Settings
        {
            ModelSourceCivitAiEnabled = civitai,
            ModelSourceCivitAiUseMirror = civitaiMirror,
            ModelSourceCivitAiMirrorUrl = civitaiMirrorUrl,
            ModelSourceHuggingFaceEnabled = hf,
            HuggingFaceApiToken = hfToken,
            ModelSourceHuggingFaceUseMirror = hfMirror,
            ModelSourceHuggingFaceMirrorUrl = hfMirrorUrl,
        };

    [Fact]
    public void CreateCivitAi_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(civitai: false);
        var http = new HttpClient();
        var result = ModelSourceFactory.CreateCivitAi(settings, http);
        Assert.Null(result);
    }

    [Fact]
    public void CreateHuggingFace_Disabled_ReturnsNull()
    {
        var settings = MakeSettings(hf: false);
        var http = new HttpClient();
        var result = ModelSourceFactory.CreateHuggingFace(settings, http);
        Assert.Null(result);
    }

    [Fact]
    public void CreateAll_ResolvesMirrorUrl_And_StripsTrailingSlash()
    {
        var settings = MakeSettings(
            civitai: true, civitaiMirror: true, civitaiMirrorUrl: "https://my-mirror.example.com/civitai/",
            hf: true, hfMirror: true, hfMirrorUrl: "https://my-mirror.example.com/hf/");
        var http = new HttpClient();
        var sources = ModelSourceFactory.CreateAll(settings, http);
        Assert.Equal(2, new List<IModelSource>(sources).Count);
    }
}
