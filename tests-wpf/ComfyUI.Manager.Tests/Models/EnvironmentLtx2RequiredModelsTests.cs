using System.IO;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class EnvironmentLtx2RequiredModelsTests
{
    [Fact]
    public void Ltx2RequiredModels_LTXVideo_Returns5AbsolutePaths()
    {
        var env = new Environment
        {
            TemplateKind = "LTXVideo",
            ModelsDirectory = "D:/models",
        };
        var paths = env.Ltx2RequiredModels;
        Assert.Equal(5, paths.Count);
        foreach (var p in paths)
        {
            Assert.StartsWith("D:\\models\\ltx-2.5\\", p);
            Assert.EndsWith(".safetensors", p);
        }
    }

    [Fact]
    public void Ltx2RequiredModels_NonLTXVideo_ReturnsEmpty()
    {
        var env = new Environment { TemplateKind = "ComfyUI", ModelsDirectory = "D:/models" };
        Assert.Empty(env.Ltx2RequiredModels);

        var env2 = new Environment { TemplateKind = "Forge", ModelsDirectory = "D:/models" };
        Assert.Empty(env2.Ltx2RequiredModels);
    }

    [Fact]
    public void Ltx2RequiredModels_LTXVideo_NamesMatch_HFQuickStart()
    {
        // https://huggingface.co/Lightricks/LTX-2.5 quick start 命令列出的 5 个模型
        var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = "M" };
        var paths = env.Ltx2RequiredModels;
        Assert.Contains(paths, p => p.Contains("diffusion_models") && p.Contains("22b-distilled-transformer"));
        Assert.Contains(paths, p => p.Contains("text_encoders") && p.Contains("gemma4-12b-with-proj"));
        Assert.Contains(paths, p => p.Contains("video-vae"));
        Assert.Contains(paths, p => p.Contains("audio-vae"));
        Assert.Contains(paths, p => p.Contains("latent_upscale_models") && p.Contains("latent-spatial-upscaler"));
    }

    [Fact]
    public void Ltx2RequiredModels_EmptyModelsDirectory_ReturnsEmpty()
    {
        var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = "" };
        Assert.Empty(env.Ltx2RequiredModels);
    }
}