using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTests
{
    private const string ProjectRoot = @"D:\ToolDevelop\ComfyUI";

    [Fact]
    public void Apply_TemplatePythonDir_EmptyDefaultsToPython()
    {
        // template paths:空字段填默认子目录名(指向 package 自带的 portable python/)
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("python", s.TemplatePythonDir);
    }

    [Fact]
    public void Apply_TemplateComfyuiDir_EmptyDefaultsToComfyUI()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);
    }

    [Fact]
    public void Apply_UserConfiguredPaths_EmptyStaysEmpty()
    {
        // EnvsDir / GlobalNodesDir 默认保持空(用户主动管理,服务层在使用时报错)
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("", s.EnvsDir);
        Assert.Equal("", s.GlobalNodesDir);
    }

    [Fact]
    public void Apply_DoesNotOverwriteRelativeExistingValues()
    {
        // 用户已经填了相对路径 → 不动
        var s = new Settings
        {
            TemplatePythonDir = "E:\\my-python",
            EnvsDir = "my-envs",
            GlobalNodesDir = "shared-nodes",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("E:\\my-python", s.TemplatePythonDir);
        Assert.Equal("my-envs", s.EnvsDir);
        Assert.Equal("shared-nodes", s.GlobalNodesDir);
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);   // 空字段填默认
    }

    [Fact]
    public void Apply_MigratesAbsolutePathUnderProjectRoot_ToRelative()
    {
        // 兼容旧 settings.json:绝对路径若落在 projectRoot 下,转相对(剥掉前缀)
        var s = new Settings
        {
            EnvsDir = @"D:\ToolDevelop\ComfyUI\bin\Debug\net8.0-windows\envs",
            TemplateComfyuiDir = @"D:\ToolDevelop\ComfyUI\ComfyUI",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"bin\Debug\net8.0-windows\envs", s.EnvsDir);
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);
    }

    [Fact]
    public void Apply_PreservesAbsolutePathOutsideProjectRoot()
    {
        // 用户故意选别处的绝对路径(如外部盘) → 保留,不强行改
        var s = new Settings
        {
            EnvsDir = @"E:\external\envs",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"E:\external\envs", s.EnvsDir);
    }

    [Fact]
    public void Apply_NullSettings_NoOp()
    {
        SettingsDefaults.Apply(null!, ProjectRoot);
    }

    [Fact]
    public void Apply_KeepsRelativePathUntouched()
    {
        // 设置页填了相对路径,Apply 不重新格式化或加 ../ 前缀
        var s = new Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "..\\external-python",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("envs", s.EnvsDir);
        Assert.Equal("..\\external-python", s.TemplatePythonDir);
    }
}