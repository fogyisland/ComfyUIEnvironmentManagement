using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class TemplateConfigDefaultsLtxVideoTests
{
    [Fact]
    public void LTXVideo_Name_IsLTXVideo2()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTXVideo", cfg.Name);  // Name 保持不变(显示在 env list 不动)
    }

    [Fact]
    public void LTXVideo_Kind_IsLTXVideo()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTXVideo", cfg.Kind);
    }

    [Fact]
    public void LTXVideo_GitHubRepoUrl_IsLTX2()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("https://github.com/Lightricks/LTX-2.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void LTXVideo_EntryScript_IsWrapperBat()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("run-ltx2-distilled.bat", cfg.EntryScript);
    }

    [Fact]
    public void LTXVideo_EntryArgs_ContainsModelsPlaceholder()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Contains("{models}", cfg.EntryArgs);
        Assert.Contains("{env}", cfg.EntryArgs);
        Assert.DoesNotContain("{port}", cfg.EntryArgs);   // CLI 模式无 web 端口
    }

    [Fact]
    public void LTXVideo_ModelsSubdir_IsModels_Ltx25()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("Models/ltx-2.5", cfg.ModelsSubdir);
    }

    [Fact]
    public void LTXVideo_LocalSourceDir_IsLTX_Video()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTX-Video", cfg.LocalSourceDir);
    }
}
