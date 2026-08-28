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
    public void GetEnvModelsDir_Forge_ReturnsStableDiffusionSubdir()
    {
        // v1.0.0.x: A1111 已下线,Forge 沿用 ModelsSubdir = "models/Stable-diffusion"
        // (webui.py 的 stable diffusion 模型目录约定)。
        var env = new Environment
        {
            Id = "e2", Name = "e2",
            TemplateKind = "Forge",
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

    /// <summary>
    /// v1.0.0.x: dev build 启动按钮路径 bug 回归 — GetEnvModelsDir 同样有硬编码
    /// <c>Path.Combine(projectRoot, "envs", env.Name, ...)</c>。修法跟 BuildStartCommand 同:
    /// env.RootPath 优先,projectRoot + "envs" 兜底。
    /// </summary>
    [Fact]
    public void GetEnvModelsDir_RootPathSet_UsesAbsoluteRootPathIgnoringProjectRoot()
    {
        var env = new Environment
        {
            Id = "e-dev", Name = "faceswap",
            TemplateKind = "ComfyUI",
            RootPath = @"D:\real-env",
            TemplateConfigSnapshot = new TemplateConfig { ModelsSubdir = "models" },
        };
        var dir = ModelSymlinker.GetEnvModelsDir(env, projectRoot: @"D:\fake-bin");
        Assert.Equal(@"D:\real-env\models", dir);
    }
}
