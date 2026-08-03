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
        return new Settings
        {
            TemplatePythonDir = templatePythonDir,
            TemplateComfyuiDir = "ComfyUI",
            DefaultPythonVersion = defaultPythonVersion,
            PythonInterpreters = pythonInterpreters ?? new(),
            ActivePythonInterpreterName = activePythonInterpreterName,
        };
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
        var comfyuiDir = Path.Combine(projectRoot, "ComfyUI");
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
        var (root, py, cm) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(
                creator: null!,
                settings: MakeSettings("3.10", "python", interpreters, activeName),
                projectRoot: root,
                recentBasePythonPath: null);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
            Assert.Null(vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_LeavesPythonExeBlank_WhenPythonTemplateMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var cm = Path.Combine(root, "ComfyUI");
        Directory.CreateDirectory(cm);
        File.WriteAllText(Path.Combine(cm, "main.py"), "");
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10"), root);
            Assert.Equal("", vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
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
            Assert.Equal(cm, vm.ComfyuiSource);
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
        var (root, py, cm) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            vm.PythonExe = "";  // 模拟用户清空
            vm.ApplyTemplate();
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApplyTemplateCommand_ReappliesTemplate()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            vm.PythonExe = "C:\\user-overridden";
            vm.ApplyTemplateCommand.Execute(null);
            Assert.Equal(py, vm.PythonExe);
            Assert.Equal(cm, vm.ComfyuiSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Layout_DoesNotRefillOnChange()
    {
        var (root, py, cm) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var vm = new CreateEnvDialogViewModel(null!, MakeSettings("3.10", "python", interpreters, activeName), root);
            vm.PythonExe = "C:\\user-overridden";
            vm.Layout = "independent";   // 切 layout
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
}