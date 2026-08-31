using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31): 锁 OpenVoice factory 默认值 —
/// <c>EntryScript = openvoice/openvoice_app.py</c>(真实 Gradio entry), <c>--share</c>。
/// 防止未来手抖改回 <c>api.py</c>(library,不是 server entry)。
/// </summary>
public sealed class TemplateConfigDefaultsOpenVoiceTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void OpenVoice_Name_IsOpenVoice()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("OpenVoice", cfg.Name);
    }

    [Fact]
    public void OpenVoice_Kind_IsOpenVoice()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("OpenVoice", cfg.Kind);
    }

    [Fact]
    public void OpenVoice_LocalSourceDir_IsOpenVoice()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("OpenVoice", cfg.LocalSourceDir);
    }

    [Fact]
    public void OpenVoice_SourceKind_IsGitHub()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind);
    }

    [Fact]
    public void OpenVoice_GitHubRepoUrl_IsMyshellAi()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("https://github.com/myshell-ai/OpenVoice.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void OpenVoice_EntryScript_IsOpenvoiceAppNotApiPy()
    {
        // 关键锁:api.py 是 library (BaseSpeakerTTS, ToneColorConverter),不是 server entry
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("openvoice/openvoice_app.py", cfg.EntryScript);
        Assert.NotEqual("api.py", cfg.EntryScript);
    }

    [Fact]
    public void OpenVoice_EntryArgs_IsShareNoPort()
    {
        // openvoice_app.py argparse 只接受 --share;port 走 GRADIO_SERVER_PORT env var
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("--share", cfg.EntryArgs);
        Assert.DoesNotContain("--port", cfg.EntryArgs);
        Assert.DoesNotContain("{port}", cfg.EntryArgs);
    }

    [Fact]
    public void OpenVoice_ModelsSubdir_IsOutputs()
    {
        var cfg = TemplateConfigDefaults.OpenVoice(ProjectRoot);
        Assert.Equal("outputs", cfg.ModelsSubdir);
    }
}