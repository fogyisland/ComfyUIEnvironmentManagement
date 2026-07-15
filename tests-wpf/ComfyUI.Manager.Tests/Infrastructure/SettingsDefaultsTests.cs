using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTests
{
    private const string ProjectRoot = @"D:\ToolDevelop\ComfyUI";

    [Fact]
    public void Apply_EmptyFieldsStayEmpty()
    {
        // 不主动填默认值 —— 路径没填就保持空,由服务层在使用时报错引导用户配置。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("", s.TemplatePythonDir);
        Assert.Equal("", s.TemplateComfyuiDir);
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
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("E:\\my-python", s.TemplatePythonDir);
        Assert.Equal("my-envs", s.EnvsDir);
        Assert.Equal("", s.TemplateComfyuiDir);   // 空字段保持空
        Assert.Equal("", s.GlobalNodesDir);      // 空字段保持空
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
    public void Apply_TreatsWhitespaceAsEmpty()
    {
        var s = new Settings
        {
            TemplatePythonDir = "   ",
            EnvsDir = "\t",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("   ", s.TemplatePythonDir); // whitespace 视为空,保持原样
        Assert.Equal("\t", s.EnvsDir);
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
