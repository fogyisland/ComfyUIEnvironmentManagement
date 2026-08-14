using System.Collections.Generic;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.15 T3:EnvPickerDialog XAML STA-thread headless load test。follow
/// <see cref="CatalogEntryPickerDialogLoadTests"/> 模式用 StaFact.RunOnSTA 把
/// XAML 解析 + InitializeComponent 调到 STA thread(避免 WPF STA 线程模型错误)。
/// 任一资源 key 缺失 / XAML 写法错都会在 ctor 阶段抛 XamlParseException。
/// </summary>
public class EnvPickerDialogLoadTests
{
    [Fact]
    public void Constructor_LoadsXaml_NoException()
    {
        var envs = new List<EnvOption> { new("env-1", "prod") };
        StaFact.RunOnSTA(() =>
        {
            var vm = new EnvPickerDialogViewModel(envs);
            var dlg = new EnvPickerDialog(vm, "test title");
            Assert.NotNull(dlg);
        });
    }
}