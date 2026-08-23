using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogBuildTemplateConfigTests
{
    private static string TempDbPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"comfy-mgr-t7r1-{System.Guid.NewGuid():N}.db");

    private (CreateEnvDialogViewModel vm, Settings settings) BuildVm(
        Dictionary<string, TemplateConfig>? templates = null,
        string projectRoot = "C:/fake-root")
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
            new VenvCreator(), new JunctionLinker(), settings, projectRoot);
        var vm = new CreateEnvDialogViewModel(creator, settings, projectRoot);
        return (vm, settings);
    }

    [Fact]
    public void BuildTemplateConfig_RelativeLocalSourceDir_JoinsWithProjectRoot()
    {
        // v1.0.0 T7 R1:ApplyTemplate and SelectedTemplateKind auto-fill store the
        // raw LocalSourceDir ("Templates/ComfyUI"). BuildTemplateConfig must
        // resolve relative paths against projectRoot so EnvCreatorService.CreateAsync
        // does not throw TEMPLATE_SOURCE_NOT_FOUND.
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "ComfyUI";
        // ApplyTemplate may have cleared ComfyuiSource if the directory does not
        // exist on disk in the test env — override with the raw relative value.
        vm.ComfyuiSource = "Templates/ComfyUI";
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal(Path.Combine("C:/fake-root", "Templates/ComfyUI"), cfg.LocalSourceDir);
    }

    [Fact]
    public void BuildTemplateConfig_AbsoluteLocalSourceDir_PassesThroughUnchanged()
    {
        // Absolute paths must not be re-joined with projectRoot — that would
        // produce nonsense like "C:/fake-root/C:/absolute/...".
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "ComfyUI";
        var abs = Path.IsPathRooted("C:/fake-root")
            ? @"C:\absolute\path\to\template"
            : "/tmp/abs/path";
        vm.ComfyuiSource = abs;
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal(abs, cfg.LocalSourceDir);
    }

    [Fact]
    public void BuildTemplateConfig_KindMatchesSelectedTemplateKind()
    {
        // The Kind on the produced TemplateConfig must reflect the user's selection,
        // not always default to ComfyUI.
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "A1111";
        vm.ComfyuiSource = "Templates/A1111";
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal("A1111", cfg.Kind);
    }
}
