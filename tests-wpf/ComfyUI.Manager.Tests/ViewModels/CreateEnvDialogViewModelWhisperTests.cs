using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-08-31):锁 <see cref="CreateEnvDialogViewModel.BuildTemplateConfig"/>
/// 对 Whisper kind 的字段传播 — <c>EntryScript="whisper"</c>(console-script 名)、
/// <c>EntryArgs=""</c>(CLI 必填 audio file + --model 在 UserExtraArgs 拼)、
/// <c>ModelsSubdir=""</c>(Whisper 自己管 ~/.cache/whisper)。
///
/// Whisper 不需要 Fix wave 1 那种新加 field propagation(对比 Fooocus 的 FooocusEntryMode
/// 是新增字段)— BuildTemplateConfig 已 propagate EntryScript / EntryArgs / UserExtraArgs /
/// ModelsSubdir 等既有字段,Whisper factory 改 EntryArgs 不会让 BuildTemplateConfig 丢。
///
/// 主要防 regression:Settings.Templates["Whisper"] 的 factory defaults 不被未来手抖改,
/// 防止 TemplateConfigDefaultsOpenVoiceTests 类似 pattern lock 住 field propagation。
/// </summary>
public sealed class CreateEnvDialogViewModelWhisperTests
{
    private static Settings BuildSettingsWithWhisper(TemplateConfig whisperTemplate)
    {
        // anchor + .git 跟 FooocusPropagationTests 同 pattern — BuildTemplateConfig 不
        // 走 TemplateOptions,但 ctor 里的 ApplyTemplate() 会读 LocalSourceDir 安全起见
        // 仍 seed 锚点。
        var anchor = Path.Combine(Path.GetTempPath(), "T-whisper-anchor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(anchor);
        var dir = Path.Combine(anchor, "WhisperTemplate");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));

        var s = new Settings { SystemTemplateLibraryDir = anchor };
        s.Templates["Whisper"] = whisperTemplate;
        return s;
    }

    private static TemplateConfig MakeWhisperTemplate() => new()
    {
        Kind = "Whisper",
        Name = "Whisper",
        LocalSourceDir = "WhisperTemplate",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/openai/whisper.git",
        EntryScript = "whisper",
        EntryArgs = "",  // v1.0.0.x (2026-08-31):Whisper CLI 必填 audio + --model 在 UserExtraArgs
        ModelsSubdir = "",
        UserExtraArgs = "",
    };

    [Fact]
    public void BuildTemplateConfig_Whisper_PropagatesEmptyEntryArgs()
    {
        // 关键锁:Whisper factory EntryArgs="" 传到 BuildTemplateConfig,不让未来
        // BuildTemplateConfig 改成 hardcode "--model tiny" 之类
        var s = BuildSettingsWithWhisper(MakeWhisperTemplate());
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Whisper" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("", cfg.EntryArgs);
        Assert.NotEqual("--model tiny", cfg.EntryArgs);
    }

    [Fact]
    public void BuildTemplateConfig_Whisper_PropagatesConsoleScriptEntryScript()
    {
        // EntryScript="whisper"(console-script 名,BuildStartCommand Whisper 分支 ignore)
        var s = BuildSettingsWithWhisper(MakeWhisperTemplate());
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Whisper" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("whisper", cfg.EntryScript);
        Assert.NotEqual("whisper/__main__.py", cfg.EntryScript);
        Assert.NotEqual("whisper.exe", cfg.EntryScript);
    }

    [Fact]
    public void BuildTemplateConfig_Whisper_PropagatesUserExtraArgs()
    {
        // 用户在 env-create dialog UserExtraArgs 字段填完整 whisper CLI 命令
        // (--model tiny C:/path/to/audio.wav),BuildTemplateConfig 必须原样传
        var t = MakeWhisperTemplate();
        t.UserExtraArgs = "--model tiny C:/audio/sample.wav";
        var s = BuildSettingsWithWhisper(t);
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Whisper" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("--model tiny C:/audio/sample.wav", cfg.UserExtraArgs);
    }

    [Fact]
    public void BuildTemplateConfig_Whisper_PropagatesEmptyModelsSubdir()
    {
        // Whisper 自己管理模型下载到 ~/.cache/whisper/,不需要 ModelsSubdir junction
        var s = BuildSettingsWithWhisper(MakeWhisperTemplate());
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Whisper" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("", cfg.ModelsSubdir);
    }

    [Fact]
    public void BuildTemplateConfig_Whisper_PreservesKindAndName()
    {
        var s = BuildSettingsWithWhisper(MakeWhisperTemplate());
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Whisper" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("Whisper", cfg.Kind);
        Assert.Equal("Whisper", cfg.Name);
        // 注意:SourceKind / GitHubRepoUrl 是 factory defaults 字段(锁在
        // TemplateConfigDefaultsWhisperTests 里),不是 BuildTemplateConfig 责任 —
        // BuildTemplateConfig 只 propagate 用户可在 dialog 编辑的字段
        // (LocalSourceDir / EntryScript / EntryArgs / UserExtraArgs / ModelsSubdir 等)。
    }

    [Fact]
    public void BuildTemplateConfig_NonWhisperKind_DoesNotInheritWhisperEntryArgs()
    {
        // 回归保护:ComfyUI kind 不被 Whisper 串味 — BuildTemplateConfig 只读
        // SelectedTemplateKind 自己的 TemplateConfig,不是读 _settings.Templates 第一个
        var anchor = Path.Combine(Path.GetTempPath(), "T-comfy-anchor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(anchor);
        var dir = Path.Combine(anchor, "ComfyUITemplate");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));

        var s = new Settings { SystemTemplateLibraryDir = anchor };
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI",
            Name = "ComfyUI",
            LocalSourceDir = "ComfyUITemplate",
            EntryScript = "main.py",
            EntryArgs = "--port {port}",
        };
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "ComfyUI" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("ComfyUI", cfg.Kind);
        Assert.Equal("main.py", cfg.EntryScript);
        Assert.Equal("--port {port}", cfg.EntryArgs);
        Assert.NotEqual("whisper", cfg.EntryScript);
        Assert.NotEqual("", cfg.EntryArgs);
    }
}