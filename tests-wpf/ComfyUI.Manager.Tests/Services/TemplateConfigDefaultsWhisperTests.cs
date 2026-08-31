using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-31):锁 Whisper factory 默认值 —
/// <c>EntryScript = "whisper"</c>(console-script 名,BuildStartCommand Whisper 分支忽略),
/// <c>EntryArgs = ""</c>(空 — 用户必填 positional audio file + --model 在 UserExtraArgs)。
///
/// Whisper 是 one-shot CLI 工具,跟 OpenVoice Gradio 服务端 pattern 不同 —
/// OpenVoice 完整 launch command 是 <c>openvoice_app.py --share</c>(无需 positional),
/// Whisper 必须有 positional audio(Whisper CLI 设计)。
/// </summary>
public sealed class TemplateConfigDefaultsWhisperTests
{
    private const string ProjectRoot = "D:/proj";

    [Fact]
    public void Whisper_Name_IsWhisper()
    {
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("Whisper", cfg.Name);
    }

    [Fact]
    public void Whisper_Kind_IsWhisper()
    {
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("Whisper", cfg.Kind);
    }

    [Fact]
    public void Whisper_LocalSourceDir_IsWhisper()
    {
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("Whisper", cfg.LocalSourceDir);
    }

    [Fact]
    public void Whisper_SourceKind_IsGitHub()
    {
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind);
    }

    [Fact]
    public void Whisper_GitHubRepoUrl_IsOpenAiWhisper()
    {
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("https://github.com/openai/whisper.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void Whisper_EntryScript_IsWhisperConsoleScriptName()
    {
        // 锁住 EntryScript="whisper" 防止未来手抖改 —
        // 这是 console-script 名(PATH 上 whisper.exe),不是文件路径。
        // BuildStartCommand Whisper 分支 ignore 它,只 UserExtraArgs 走命令行。
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("whisper", cfg.EntryScript);
    }

    [Fact]
    public void Whisper_EntryArgs_IsEmpty()
    {
        // 关键锁:EntryArgs 默认空 — Whisper CLI 必须有 positional audio file,
        // 不能在 factory 替用户选 audio 路径(audio 是 env-specific 配置)。
        // 旧值 "--model tiny" 缺 audio,二级问题 — 改成空让用户 UserExtraArgs 补全。
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("", cfg.EntryArgs);
        Assert.NotEqual("--model tiny", cfg.EntryArgs);
    }

    [Fact]
    public void Whisper_ModelsSubdir_IsEmpty()
    {
        // Whisper 自己管理模型下载到 ~/.cache/whisper/,不需要 ModelsSubdir junction
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("", cfg.ModelsSubdir);
    }

    [Fact]
    public void Whisper_UserExtraArgs_IsEmpty()
    {
        // UserExtraArgs 由用户在 env-create dialog 填 (audio file + --model),default 空
        var cfg = TemplateConfigDefaults.Whisper(ProjectRoot);
        Assert.Equal("", cfg.UserExtraArgs);
    }
}
