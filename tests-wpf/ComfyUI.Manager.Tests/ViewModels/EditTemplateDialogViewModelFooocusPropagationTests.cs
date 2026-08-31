using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// Fix wave 1 (I2): 验证 <see cref="EditTemplateDialogViewModel.LoadFrom"/>
/// 从传入的 TemplateConfig 复制 FooocusEntryMode 到 WorkingConfig。
/// 之前 hand-copy 漏掉这一字段 → 用户开 EditTemplate 改 Fooocus 别的东西
/// (e.g. LocalSourceDir),Save() 把 WorkingConfig 写回 Settings,WorkingConfig
/// 的 FooocusEntryMode 已被 ctor 默认成 AutoUpdate → 下次 env-create 走 Stable
/// 配置的会被静默重置回 AutoUpdate。
/// </summary>
public sealed class EditTemplateDialogViewModelFooocusPropagationTests
{
    private static Settings SeedSettings() => new();

    [Fact]
    public void LoadFrom_FooocusStable_PropagatesStable()
    {
        // Arrange: existing TemplateConfig has FooocusEntryMode = Stable
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Edit };
        var existing = new TemplateConfig
        {
            Kind = "Fooocus",
            Name = "Fooocus",
            LocalSourceDir = "Fooocus",
            EntryScript = "entry.py",
            FooocusEntryMode = FooocusEntryMode.Stable,
        };

        // Act
        vm.LoadFrom(existing);

        // Assert
        Assert.Equal(FooocusEntryMode.Stable, vm.WorkingConfig.FooocusEntryMode);
    }

    [Fact]
    public void LoadFrom_FooocusAutoUpdate_PropagatesAutoUpdate()
    {
        // Mirror: AutoUpdate → WorkingConfig.FooocusEntryMode == AutoUpdate
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Edit };
        var existing = new TemplateConfig
        {
            Kind = "Fooocus",
            Name = "Fooocus",
            LocalSourceDir = "Fooocus",
            EntryScript = "entry_with_update.py",
            FooocusEntryMode = FooocusEntryMode.AutoUpdate,
        };

        vm.LoadFrom(existing);

        Assert.Equal(FooocusEntryMode.AutoUpdate, vm.WorkingConfig.FooocusEntryMode);
    }
}