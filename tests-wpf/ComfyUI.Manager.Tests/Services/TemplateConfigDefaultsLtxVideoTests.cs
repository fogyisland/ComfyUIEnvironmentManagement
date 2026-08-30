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
    public void LTXVideo_LocalSourceDir_IsLTXVideo()
    {
        // v1.0.0.x LTX-2 (T1):LocalSourceDir 跟所有其它 10 个内置对齐 = kind 名
        // (而不是品牌命名 "LTX-Video")。ENVTemplate 磁盘目录就叫 "LTXVideo/"(无连字符),
        // 跟 kind 名一致 — 用 kind 名能复用 raw == kind.Kind 的 default-seed skip
        // 逻辑,避免 StartupPathProbe / TemplateManagementSmokeTests 的 brand-name 旁路。
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTXVideo", cfg.LocalSourceDir);
    }
}
