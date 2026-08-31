using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// Fix wave 1 (C1): 验证 <see cref="CreateEnvDialogViewModel.BuildTemplateConfig"/>
/// 从 Settings.Templates 复制 FooocusEntryMode 到返回的 TemplateConfig。
/// 之前 hand-copy 漏掉这一字段 → 用户在 settings.inf 配 FooocusEntryMode = Stable,
/// 但走 "Create Env" dialog 创建 env 时,BuildTemplateConfig 把 FooocusEntryMode 改回
/// 默认 AutoUpdate,snapshot 冻结后 ProcessLauncher 永远拿不到 Stable。
/// </summary>
public sealed class CreateEnvDialogViewModelFooocusPropagationTests
{
    private static Settings BuildSettingsWithFooocus(FooocusEntryMode mode)
    {
        // 跟 CreateEnvDialogViewModelTests.MakeSettings 一样需要 anchor + .git 让
        // TemplateOptions 把 Fooocus 包进来 — 实际上 BuildTemplateConfig 走
        // _settings.Templates[SelectedTemplateKind] 不靠 TemplateOptions,但 ctor 里的
        // ApplyTemplate() 会读 LocalSourceDir,跟 TemplateOptions 行为解耦。安全起见
        // 仍 seed 锚点,跟兄弟测试保持一致。
        var anchor = Path.Combine(Path.GetTempPath(), "T-fooocus-anchor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(anchor);
        var dir = Path.Combine(anchor, "FooocusTemplate");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));

        var s = new Settings
        {
            SystemTemplateLibraryDir = anchor,
        };
        s.Templates["Fooocus"] = new TemplateConfig
        {
            Kind = "Fooocus",
            Name = "Fooocus",
            LocalSourceDir = "FooocusTemplate",
            EntryScript = mode == FooocusEntryMode.Stable ? "entry.py" : "entry_with_update.py",
            FooocusEntryMode = mode,
        };
        return s;
    }

    [Fact]
    public void BuildTemplateConfig_FooocusStable_PropagatesStable()
    {
        // Arrange: Settings.Templates["Fooocus"] has FooocusEntryMode = Stable
        var s = BuildSettingsWithFooocus(FooocusEntryMode.Stable);
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Fooocus" };

        // Act
        var cfg = vm.BuildTemplateConfig();

        // Assert
        Assert.Equal(FooocusEntryMode.Stable, cfg.FooocusEntryMode);
    }

    [Fact]
    public void BuildTemplateConfig_FooocusAutoUpdate_PropagatesAutoUpdate()
    {
        // Mirror: AutoUpdate default → returned cfg also AutoUpdate
        var s = BuildSettingsWithFooocus(FooocusEntryMode.AutoUpdate);
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "Fooocus" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal(FooocusEntryMode.AutoUpdate, cfg.FooocusEntryMode);
    }

    [Fact]
    public void BuildTemplateConfig_NonFooocusKind_DoesNotInheritFooocusEntryMode()
    {
        // Sanity: ComfyUI kind 的 TemplateConfig FooocusEntryMode 总是默认 AutoUpdate,
        // 不会从别的 template 串味(确认 BuildTemplateConfig 只读 SelectedTemplateKind
        // 自己的 TemplateConfig,不是读 _settings.Templates 第一个 / 全部)。
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
        };
        var vm = new CreateEnvDialogViewModel(null!, s, "") { SelectedTemplateKind = "ComfyUI" };

        var cfg = vm.BuildTemplateConfig();

        Assert.Equal("ComfyUI", cfg.Kind);
        Assert.Equal(FooocusEntryMode.AutoUpdate, cfg.FooocusEntryMode);
    }
}