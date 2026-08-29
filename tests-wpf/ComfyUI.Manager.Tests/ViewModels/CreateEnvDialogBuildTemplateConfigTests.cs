using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogBuildTemplateConfigTests
{
    /// <summary>v1.0.0.x:step 6.6 wheel seed 的 no-op fake — 本测试不调用
    /// CreateAsync,但 EnvCreatorService ctor 仍要求传入。</summary>
    private static Task NoOpWheel(string venvPython, CancellationToken ct) => Task.CompletedTask;

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
            settings.Templates["Forge"] = new TemplateConfig
            {
                Kind = "Forge", LocalSourceDir = "Templates/Forge",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
        }
        // LocalDataPaths is sealed — use SqliteConnectionFactory(string) test seam.
        // v1.0.0.x:env-create step 6.6 wheel seed 默认会跑真 `python -m pip install wheel`。
        // 本测试不调用 CreateAsync(只测 BuildTemplateConfig),但 ctor 必须接受参数;
        // 注入 no-op 即可。
        var creator = new EnvCreatorService(
            new SqliteConnectionFactory(TempDbPath()),
            new VenvCreator(), new JunctionLinker(), settings, projectRoot,
            pipInstallWheelAsync: NoOpWheel);
        var vm = new CreateEnvDialogViewModel(creator, settings, projectRoot);
        return (vm, settings);
    }

    [Fact]
    public void BuildTemplateConfig_RelativeLocalSourceDir_PassesThroughUnchanged()
    {
        // v1.0.0.x bug fix:BuildTemplateConfig 不预先 resolve 路径 — 锚点跟 Service 端
        // TemplatePathResolver.Resolve(localSourceDir, _settings.SystemTemplateLibraryDir)
        // 不一致会埋坑(用户配 SystemTemplateLibraryDir = D:\…\ENVTemplate 时,相对路径
        // "ComfyUI" 应解析为 ENVTemplate\ComfyUI,但 Dialog 拼成 projectRoot\ComfyUI
        // → Service Directory.Exists false → TEMPLATE_SOURCE_NOT_FOUND)。
        // Dialog 这里原样传 raw LocalSourceDir,Service 端按 SystemTemplateLibraryDir
        // 锚定 resolve 才是权威。
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "ComfyUI";
        vm.TemplateSource = "Templates/ComfyUI";
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal("Templates/ComfyUI", cfg.LocalSourceDir);
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
        vm.TemplateSource = abs;
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal(abs, cfg.LocalSourceDir);
    }

    [Fact]
    public void BuildTemplateConfig_KindMatchesSelectedTemplateKind()
    {
        // The Kind on the produced TemplateConfig must reflect the user's selection,
        // not always default to ComfyUI.
        var (vm, _) = BuildVm();
        vm.SelectedTemplateKind = "Forge";
        vm.TemplateSource = "Templates/Forge";
        var cfg = vm.BuildTemplateConfig();
        Assert.Equal("Forge", cfg.Kind);
    }
}
