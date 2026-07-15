using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTests
{
    private const string ProjectRoot = @"D:\ToolDevelop\ComfyUI";

    [Fact]
    public void Apply_FillsAllEmptyPathFieldsWithRelativeSubdirs()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("template-python", s.TemplatePythonDir);
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);
        Assert.Equal("envs", s.EnvsDir);
        Assert.Equal("global-nodes", s.GlobalNodesDir);
    }

    [Fact]
    public void Apply_DoesNotOverwriteRelativeExistingValues()
    {
        var s = new Settings
        {
            TemplatePythonDir = "E:\\my-python",
            EnvsDir = "my-envs",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("E:\\my-python", s.TemplatePythonDir); // 相对路径保留
        Assert.Equal("my-envs", s.EnvsDir);                 // 相对路径保留
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);      // 空字段填默认
        Assert.Equal("global-nodes", s.GlobalNodesDir);      // 空字段填默认
    }

    [Fact]
    public void Apply_MigratesAbsolutePathUnderProjectRoot_ToRelative()
    {
        var s = new Settings
        {
            EnvsDir = @"D:\ToolDevelop\ComfyUI\bin\Debug\net8.0-windows\envs",
            TemplateComfyuiDir = @"D:\ToolDevelop\ComfyUI\ComfyUI",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        // 剥掉 projectRoot 前缀,转相对
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

        Assert.Equal("template-python", s.TemplatePythonDir);
        Assert.Equal("envs", s.EnvsDir);
    }

    [Fact]
    public void Apply_NullSettings_NoOp()
    {
        SettingsDefaults.Apply(null!, ProjectRoot);
    }

    [Fact]
    public void Apply_DefaultsAreNeverAbsolute()
    {
        // 防御:默认子目录名不能含绝对路径成分
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        foreach (var path in new[] {
            s.TemplatePythonDir, s.TemplateComfyuiDir,
            s.EnvsDir, s.GlobalNodesDir })
        {
            Assert.False(Path.IsPathRooted(path),
                $"默认值不应是绝对路径: {path}");
        }
    }
}