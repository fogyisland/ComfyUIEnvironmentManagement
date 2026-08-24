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
        if (templates != null)
        {
            settings.Templates = templates;
        }
        else
        {
            settings.Templates["ComfyUI"] = new TemplateConfig
            {
                Kind = "ComfyUI", LocalSourceDir = "Templates/ComfyUI",
                EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models",
            };
            settings.Templates["A1111"] = new TemplateConfig
            {
                Kind = "A1111", LocalSourceDir = "Templates/A1111",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
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
        Assert.Contains("A1111", kinds);
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
        vm.SelectedTemplateKind = "A1111";
        Assert.Equal("Templates/A1111", vm.TemplateSource);
    }

    [Fact]
    public void CanConfirm_ValidNameAndPython_ReturnsTrue()
    {
        var (vm, _) = BuildVm();
        vm.Name = "myEnv";
        vm.PythonExe = "python";
        vm.SelectedTemplateKind = "ComfyUI";
        vm.TemplateSource = "Templates/ComfyUI";
        Assert.True(vm.CanConfirm);
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