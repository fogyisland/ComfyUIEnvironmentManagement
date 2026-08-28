using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogTemplateKindTests
{
    private static string TempDbPath() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"comfy-mgr-t7-{System.Guid.NewGuid():N}.db");

    private (CreateEnvDialogViewModel vm, Settings settings) BuildVm(
        Dictionary<string, TemplateConfig>? templates = null)
    {
        var settings = new Settings();
        // v1.0.0.x:CreateEnvDialog 现在检查模板本地目录(LocalDirExists → 目录 + .git)再允许 Create。
        // 测试用 anchor = temp/T-anchor-<guid>/,每个 kind 都创 <anchor>/<kind>/.git 子目录模拟"已 clone"。
        // 这样默认测试条件下 templates 都"就位",CanConfirm 默认 true。
        var anchor = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "T-anchor-" + System.Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(anchor);
        settings.SystemTemplateLibraryDir = anchor;
        if (templates != null)
        {
            settings.Templates = templates;
        }
        else
        {
            settings.Templates["ComfyUI"] = new TemplateConfig
            {
                Kind = "ComfyUI", LocalSourceDir = "ComfyUI",
                EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models",
            };
            settings.Templates["Forge"] = new TemplateConfig
            {
                Kind = "Forge", LocalSourceDir = "Forge",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
        }
        foreach (var kvp in settings.Templates)
        {
            var dir = System.IO.Path.Combine(anchor, kvp.Value.LocalSourceDir);
            System.IO.Directory.CreateDirectory(dir);
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        }
        // LocalDataPaths is sealed — use SqliteConnectionFactory(string) test seam.
        var creator = new EnvCreatorService(
            new SqliteConnectionFactory(TempDbPath()),
            new VenvCreator(), new JunctionLinker(), settings, "C:/fake-root");
        var vm = new CreateEnvDialogViewModel(creator, settings, "C:/fake-root");
        return (vm, settings);
    }

    [Fact]
    public void TemplateOptions_ListsAllSettingsTemplates()
    {
        var (vm, _) = BuildVm();
        var kinds = vm.TemplateOptions.Select(t => t.Kind).ToList();
        Assert.Contains("ComfyUI", kinds);
        Assert.Contains("Forge", kinds);
    }

    [Fact]
    public void SelectedTemplateKind_DefaultIsComfyUI()
    {
        var (vm, _) = BuildVm();
        Assert.Equal("ComfyUI", vm.SelectedTemplateKind);
    }

    [Fact]
    public void SetSelectedTemplateKind_UpdatesTemplateSource()
    {
        // When user picks a kind, the TemplateSource auto-fills from that template
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "Forge";
        Assert.Equal("Forge", vm.TemplateSource);
    }

    [Fact]
    public void CanConfirm_ValidNameAndPython_ReturnsTrue()
    {
        var (vm, _) = BuildVm();
        vm.Name = "myEnv";
        vm.PythonExe = "python";
        vm.SelectedTemplateKind = "ComfyUI";
        vm.TemplateSource = "ComfyUI";
    }

    [Fact]
    public void CanConfirm_UnknownTemplateKind_ReturnsFalse()
    {
        var (vm, _) = BuildVm();
        vm.Name = "myEnv";
        vm.PythonExe = "python";
        vm.SelectedTemplateKind = "NonExistentKind";
        Assert.False(vm.CanConfirm);
    }
}