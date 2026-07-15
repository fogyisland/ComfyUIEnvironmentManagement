using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTests
{
    [Fact]
    public void Apply_FillsAllEmptyPathFields()
    {
        var s = new Settings();
        var projectRoot = @"D:\ToolDevelop\ComfyUI";

        SettingsDefaults.Apply(s, projectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\template-python", s.TemplatePythonDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ComfyUI", s.TemplateComfyuiDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\envs", s.EnvsDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\global-nodes", s.GlobalNodesDir);
    }

    [Fact]
    public void Apply_DoesNotOverwriteExistingValues()
    {
        var s = new Settings
        {
            TemplatePythonDir = @"E:\my-python",
            EnvsDir = @"F:\my-envs",
            // TemplateComfyuiDir + GlobalNodesDir 留空,看是否会被填
        };
        var projectRoot = @"D:\ToolDevelop\ComfyUI";

        SettingsDefaults.Apply(s, projectRoot);

        Assert.Equal(@"E:\my-python", s.TemplatePythonDir); // 保留
        Assert.Equal(@"F:\my-envs", s.EnvsDir);             // 保留
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ComfyUI", s.TemplateComfyuiDir); // 填空
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\global-nodes", s.GlobalNodesDir); // 填空
    }

    [Fact]
    public void Apply_TreatsWhitespaceAsEmpty()
    {
        var s = new Settings
        {
            TemplatePythonDir = "   ",
            EnvsDir = "\t",
        };

        SettingsDefaults.Apply(s, @"C:\root");

        Assert.Equal(@"C:\root\template-python", s.TemplatePythonDir);
        Assert.Equal(@"C:\root\envs", s.EnvsDir);
    }

    [Fact]
    public void Apply_NullSettings_NoOp()
    {
        // 不抛
        SettingsDefaults.Apply(null!, @"C:\root");
    }
}