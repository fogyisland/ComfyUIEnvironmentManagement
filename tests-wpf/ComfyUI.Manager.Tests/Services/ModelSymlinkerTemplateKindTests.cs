using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSymlinkerTemplateKindTests
{
    [Fact]
    public void GetEnvModelsDir_ComfyUI_ReturnsModels()
    {
        // G8: ComfyUI default ModelsSubdir is "models"
        var env = new Environment
        {
            Id = "e1", Name = "e1",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = new TemplateConfig { ModelsSubdir = "models" },
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e1\models", dir);
    }

    [Fact]
    public void GetEnvModelsDir_A1111_ReturnsStableDiffusionSubdir()
    {
        // G8: A1111 ModelsSubdir is "models/Stable-diffusion"
        var env = new Environment
        {
            Id = "e2", Name = "e2",
            TemplateKind = "A1111",
            TemplateConfigSnapshot = new TemplateConfig { ModelsSubdir = "models/Stable-diffusion" },
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e2\models\Stable-diffusion", dir);
    }

    [Fact]
    public void GetEnvModelsDir_MissingSnapshot_FallsBackToModels()
    {
        // backward compat
        var env = new Environment
        {
            Id = "e3", Name = "e3",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = null,
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake");
        Assert.Equal(@"D:\fake\envs\e3\models", dir);
    }
}
