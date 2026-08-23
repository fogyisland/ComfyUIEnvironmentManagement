using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogViewModelTests
{
    private static Settings MakeSettings(
        string defaultPythonVersion = "3.10",
        string templatePythonDir = "python",
        List<PythonInterpreter>? pythonInterpreters = null,
        string activePythonInterpreterName = "")
    {
        // v1.0.0 T7:VM 走 Settings.Templates["ComfyUI"] 解析 ComfyuiSource,
        // 不再读 TemplateComfyuiDir。helper seed 一个默认 ComfyUI entry。
        var s = new Settings
        {
            TemplatePythonDir = templatePythonDir,
            DefaultPythonVersion = defaultPythonVersion,
            PythonInterpreters = pythonInterpreters ?? new(),
            ActivePythonInterpreterName = activePythonInterpreterName,
        };
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI", LocalSourceDir = "ComfyUITemplate",
            EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models",
        };
        return s;
    }

    private static (CreateEnvDialogViewModel vm, EnvCreatorService? creator, Settings settings, string projectRoot, string? recentBasePythonPath, Action<ComfyUI.Manager.Models.Environment?>? onResult) MakeVm(
        Settings? settings = null,
        string projectRoot = "",
        string? recentBasePythonPath = null,
        EnvCreatorService? creator = null,
        Action<ComfyUI.Manager.Models.Environment?>? onResult = null)
    {
        settings ??= MakeSettings();
        return (
            new CreateEnvDialogViewModel(creator!, settings, projectRoot, recentBasePythonPath, onResult),
            creator,
            settings,
            projectRoot,
            recentBasePythonPath,
            onResult);
    }

    private static (string projectRoot, string pythonExe, string comfyuiDir) CreateTemplateTree(string version)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var pythonExe = Path.Combine(projectRoot, "python", version, "python.exe");
        var comfyuiDir = Path.Combine(projectRoot, "ComfyUITemplate");
        Directory.CreateDirectory(Path.GetDirectoryName(pythonExe)!);
        Directory.CreateDirectory(comfyuiDir);
        File.WriteAllText(pythonExe, "");
        File.WriteAllText(Path.Combine(comfyuiDir, "main.py"), "");
        return (projectRoot, pythonExe, comfyuiDir);
    }

    /// <summary>
    /// v0.6.5.6 helper: tests that need active interpreter resolution to a real
    /// existing python.exe use this to build the (interpreter list, active name) pair.
    /// </summary>
    private static (List<PythonInterpreter> interpreters, string activeName) ActiveAt(string pythonExe) => (
        new() { new() { Name = "default", Path = pythonExe } }, "default");

    [Fact]
    public void Constructor_AppliesTemplateOnInit_WhenBothTemplatesPresent()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(
                creator: null!,
                settings: MakeSettings("3.10", "python", interpreters, activeName),
                projectRoot: root,
                recentBasePythonPath: null);
            Assert.Equal(py, vm.PythonExe);
            // v1.0.0 T7:ComfyuiSource 现在存模板 raw LocalSourceDir(相对路径 "ComfyUITemplate")。
            // EnvCreatorService.CreateAsync / BuildTemplateConfig 负责把它 join projectRoot。
            Assert.Equal("ComfyUITemplate", vm.ComfyuiSource);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_LeavesPythonExeBlank_WhenPythonTemplateMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var cm = Path.Combine(root, "ComfyUITemplate");
        Directory.CreateDirectory(cm);
        File.WriteAllText(Path.Combine(cm, "main.py"), "");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal("", vm.PythonExe);
            // v1.0.0 T7:ComfyuiSource 存 raw template LocalSourceDir("ComfyUITemplate" 相对路径)
            Assert.Equal("ComfyUITemplate", vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("请在设置页添加 Python 解释器", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_LeavesComfyuiSourceBlank_WhenComfyuiTemplateMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var py = Path.Combine(root, "python", "3.10", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(py)!);
        File.WriteAllText(py, "");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal("", vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("ComfyUI", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_CombinesWarnings_WhenBothTemplatesMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal("", vm.PythonExe);
            Assert.Equal("", vm.ComfyuiSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("请在设置页添加 Python 解释器", vm.TemplateWarningMessage);
            Assert.Contains("ComfyUI", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_RespectsDefaultPythonVersion()
    {
        var (root, py, cm) = CreateTemplateTree("3.11");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(
                null!,
                MakeSettings("3.11", "python", interpreters, activeName),
                root,
                recentBasePythonPath: null);
            Assert.Equal(py, vm.PythonExe);   // active.Path 解析到 3.11 子目录
            // v1.0.0 T7:ComfyuiSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_ClearsWarning_WhenBothTemplatesPresent()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplyTemplate_PopulatesPythonExe_WhenTemplateExists()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            vm.PythonExe = "";  // 模拟用户清空
            vm.ApplyTemplate();
            Assert.Equal(py, vm.PythonExe);
            // v1.0.0 T7:ComfyuiSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplyTemplateCommand_ReappliesTemplate()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            vm.PythonExe = "C:\\user-overridden";
            vm.ApplyTemplateCommand.Execute(null);
            Assert.Equal(py, vm.PythonExe);
            // v1.0.0 T7:ComfyuiSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SelectedTemplateKind_DoesNotRefillPythonExe()
    {
        // v1.0.0 T7 替代 v0.x Layout_DoesNotRefillOnChange:切 TemplateKind 不应重填 PythonExe。
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var settings = MakeSettings("3.10", "python", interpreters, activeName);
            settings.Templates["A1111"] = new TemplateConfig
            {
                Kind = "A1111", LocalSourceDir = "Templates/A1111",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            vm.PythonExe = "C:\\user-overridden";
            vm.SelectedTemplateKind = "A1111";   // 切 template
            Assert.Equal("C:\\user-overridden", vm.PythonExe);  // 不应被覆盖
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_PrefersRecentBase_WhenFileExists()
    {
        var recentBase = Path.Combine(Path.GetTempPath(), "recent-base-" + Path.GetRandomFileName());
        File.WriteAllText(recentBase, "");
        var (root, _, _) = CreateTemplateTree("3.10");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root, recentBase);
            Assert.Equal(recentBase, vm.PythonExe);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally
        {
            File.Delete(recentBase);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_FallsBackToSettings_WhenRecentBasePathIsNull()
    {
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root, recentBasePythonPath: null);
            Assert.Equal(py, vm.PythonExe);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_FallsBackToSettings_WhenRecentBaseFileMissing()
    {
        var recentBase = Path.Combine(Path.GetTempPath(), "missing-" + Path.GetRandomFileName());
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root, recentBase);
            Assert.Equal("", vm.PythonExe);
            Assert.Contains("请在设置页添加 Python 解释器", vm.TemplateWarningMessage ?? "");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_ApplyTemplateOverridesRecentBase()
    {
        var recentBase = Path.Combine(Path.GetTempPath(), "recent-" + Path.GetRandomFileName());
        File.WriteAllText(recentBase, "");
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root, recentBase);
            Assert.Equal(recentBase, vm.PythonExe);
            vm.ApplyTemplateCommand.Execute(null);
            Assert.Equal(py, vm.PythonExe);
        }
        finally
        {
            File.Delete(recentBase);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplyTemplate_UsesActiveInterpreterPath_NotTemplateConcat()
    {
        var settings = MakeSettings(
            templatePythonDir: "D:/python",
            defaultPythonVersion: "3.10",
            pythonInterpreters: new()
            {
                new() { Name = "py3.11", Path = "/custom/py3.11/python.exe" },
            },
            activePythonInterpreterName: "py3.11");
        var (vm, _, _, _, _, _) = MakeVm(settings: settings, recentBasePythonPath: null);

        vm.ApplyTemplate();

        Assert.Equal("/custom/py3.11/python.exe", vm.PythonExe);
    }

    [Fact]
    public void ApplyTemplate_FallsBackToEmpty_WhenActiveMissing()
    {
        var settings = MakeSettings(
            templatePythonDir: "",
            defaultPythonVersion: "3.10",
            pythonInterpreters: new(),
            activePythonInterpreterName: "");
        var (vm, _, _, _, _, _) = MakeVm(settings: settings, recentBasePythonPath: null);

        vm.ApplyTemplate();

        Assert.Equal("", vm.PythonExe);
    }

    // —— 步骤进度面板(v0.6.5.6 hotfix)——

    [Fact]
    public void Steps_AreConstructedInExpectedOrder_OnInit()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        // 与 EnvCreatorService.CreateAsync emit 的 6 个 CreateStepReport 一一对应
        Assert.Equal(6, vm.Steps.Count);
        Assert.Equal("校验输入", vm.Steps[0].Name);
        Assert.Equal("分配端口", vm.Steps[1].Name);
        Assert.Equal("创建 env 根目录", vm.Steps[2].Name);
        Assert.Equal("链接 ComfyUI 源", vm.Steps[3].Name);
        Assert.Equal("创建 venv 环境", vm.Steps[4].Name);
        Assert.Equal("保存配置", vm.Steps[5].Name);
    }

    [Fact]
    public void Steps_StartAsPending_OnInit()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        foreach (var s in vm.Steps)
        {
            Assert.Equal(CreateStepStatus.Pending, s.Status);
            Assert.Null(s.Detail);
        }
    }

    [Fact]
    public void ResetSteps_ResetsAllToPending_AndClearsDetail()
    {
        var (vm, _, _, _, _, _) = MakeVm();
        vm.Steps[0].Status = CreateStepStatus.Done;
        vm.Steps[1].Status = CreateStepStatus.Failed;
        vm.Steps[1].Detail = "previous detail";

        vm.ResetSteps();

        foreach (var s in vm.Steps)
        {
            Assert.Equal(CreateStepStatus.Pending, s.Status);
            Assert.Null(s.Detail);
        }
    }

    [Fact]
    public void OnStepReport_MarksMatchingStepRunning()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        // service 实际流程:先 emit 校验输入,再 emit 分配端口
        vm.OnStepReport(new CreateStepReport("校验输入"));
        vm.OnStepReport(new CreateStepReport("分配端口", "port = 8188"));

        Assert.Equal(CreateStepStatus.Done, vm.Steps[0].Status);
        Assert.Equal(CreateStepStatus.Running, vm.Steps[1].Status);
        Assert.Equal("port = 8188", vm.Steps[1].Detail);
        Assert.Equal(CreateStepStatus.Pending, vm.Steps[2].Status);
    }

    [Fact]
    public void OnStepReport_AdvancesPreviousStepsToDone_OnSequentialEmits()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        vm.OnStepReport(new CreateStepReport("校验输入"));
        vm.OnStepReport(new CreateStepReport("分配端口"));
        vm.OnStepReport(new CreateStepReport("创建 env 根目录", "→ /tmp/envs/x"));

        Assert.Equal(CreateStepStatus.Done, vm.Steps[0].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[1].Status);
        Assert.Equal(CreateStepStatus.Running, vm.Steps[2].Status);
        Assert.Equal("→ /tmp/envs/x", vm.Steps[2].Detail);
        Assert.Equal(CreateStepStatus.Pending, vm.Steps[3].Status);
    }

    [Fact]
    public void OnStepReport_AllPreviousDone_WhenLastStepEmits()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        // 一次性推到第 6 个 step
        for (int i = 0; i < vm.Steps.Count - 1; i++)
        {
            vm.OnStepReport(new CreateStepReport(vm.Steps[i].Name));
        }
        vm.OnStepReport(new CreateStepReport("保存配置", "yaml = /tmp/extra_model_paths.yaml"));

        Assert.Equal(CreateStepStatus.Done, vm.Steps[0].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[1].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[2].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[3].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[4].Status);
        Assert.Equal(CreateStepStatus.Running, vm.Steps[5].Status);
        Assert.Equal("yaml = /tmp/extra_model_paths.yaml", vm.Steps[5].Detail);
    }

    [Fact]
    public void OnStepReport_IgnoresUnknownStepName()
    {
        var (vm, _, _, _, _, _) = MakeVm();

        vm.OnStepReport(new CreateStepReport("不存在的步骤"));

        // 所有 step 仍 Pending
        foreach (var s in vm.Steps)
        {
            Assert.Equal(CreateStepStatus.Pending, s.Status);
        }
    }
}