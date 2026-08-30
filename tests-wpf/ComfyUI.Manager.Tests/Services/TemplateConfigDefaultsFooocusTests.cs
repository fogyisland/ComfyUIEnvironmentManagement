using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class TemplateConfigDefaultsFooocusTests
{
    [Fact]
    public void Fooocus_Name_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.Name);
    }

    [Fact]
    public void Fooocus_Kind_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.Kind);
    }

    [Fact]
    public void Fooocus_LocalSourceDir_IsFooocus()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("Fooocus", cfg.LocalSourceDir);
    }

    [Fact]
    public void Fooocus_SourceKind_IsGitHub()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind);
    }

    [Fact]
    public void Fooocus_GitHubRepoUrl_IsLllyasviel()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("https://github.com/lllyasviel/Fooocus.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void Fooocus_EntryScript_IsEntryWithUpdate()
    {
        // 默认 AutoUpdate 模式:EntryScript 仍是 entry_with_update.py(现状)
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("entry_with_update.py", cfg.EntryScript);
    }

    [Fact]
    public void Fooocus_EntryArgs_ContainsPortAndListen()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Contains("{port}", cfg.EntryArgs);
        Assert.Contains("--listen", cfg.EntryArgs);
    }

    [Fact]
    public void Fooocus_ModelsSubdir_IsModels()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal("models", cfg.ModelsSubdir);
    }

    // v1.0.0.x: 新增字段 — Fooocus entry mode 默认 = AutoUpdate (0, 跟现状 100% 一致)
    [Fact]
    public void Fooocus_FooocusEntryMode_IsAutoUpdate()
    {
        var cfg = TemplateConfigDefaults.Fooocus("D:/proj");
        Assert.Equal(FooocusEntryMode.AutoUpdate, cfg.FooocusEntryMode);
    }
}