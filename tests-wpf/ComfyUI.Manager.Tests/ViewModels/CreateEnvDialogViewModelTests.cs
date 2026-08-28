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
        // v1.0.0 T7:VM 走 Settings.Templates["ComfyUI"] 解析 TemplateSource,
        // 不再读 TemplateComfyuiDir。helper seed 一个默认 ComfyUI entry。
        // v1.0.0.x: 设 SystemTemplateLibraryDir = temp anchor,创建 anchor/ComfyUITemplate/.git
        // 模拟 "已 clone 模板" — 否则 CreateEnvDialog 在 ApplyTemplate() 检测本地目录
        // 为空 → TemplateOptions 过滤掉该 template → CanConfirm=false,所有 positive 测试都 fail。
        var anchor = Path.Combine(Path.GetTempPath(), "T-anchor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(anchor);
        var s = new Settings
        {
            TemplatePythonDir = templatePythonDir,
            DefaultPythonVersion = defaultPythonVersion,
            PythonInterpreters = pythonInterpreters ?? new(),
            ActivePythonInterpreterName = activePythonInterpreterName,
            SystemTemplateLibraryDir = anchor,
        };
        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI", LocalSourceDir = "ComfyUITemplate",
            EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models",
        };
        var dir = Path.Combine(anchor, "ComfyUITemplate");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
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
            // v1.0.0 T7:TemplateSource 现在存模板 raw LocalSourceDir(相对路径 "ComfyUITemplate")。
            // EnvCreatorService.CreateAsync / BuildTemplateConfig 负责把它 join projectRoot。
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
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
            // v1.0.0 T7:TemplateSource 存 raw template LocalSourceDir("ComfyUITemplate" 相对路径)
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("请在设置页添加 Python 解释器", vm.TemplateWarningMessage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_FiltersComfyuiTemplate_WhenLocalDirMissing()
    {
        // v1.0.0.x:ComfyUI 模板本地为空时,从 TemplateOptions 过滤掉(不进 ComboBox),
        // 而不是显示"目标环境模板本地为空"警告。TemplateSource 仍显示 LocalSourceDir 文本
        // (Settings.Templates["ComfyUI"] 还在),只是选项里没 ComfyUI 这一项 + CanConfirm=false。
        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var py = Path.Combine(root, "python", "3.10", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(py)!);
        File.WriteAllText(py, "");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var settings = MakeSettings("3.10", "python", interpreters, activeName);
            // 强制 SystemTemplateLibraryDir 到不存在的 anchor → LocalDirExists=false
            settings.SystemTemplateLibraryDir = Path.Combine(Path.GetTempPath(), "no-such-anchor-" + Guid.NewGuid().ToString("N")[..8]);
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            Assert.Equal(py, vm.PythonExe);
            // v1.0.0.x:TemplateSource 仍填 LocalSourceDir(让用户看到「待 clone」目录名)
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
            // v1.0.0.x:TemplateOptions 应被过滤,ComfyUI 不出现在下拉列表
            Assert.DoesNotContain(vm.TemplateOptions, t => t.Kind == "ComfyUI");
            Assert.Empty(vm.TemplateOptions);
            // v1.0.0.x:不再追加"目标环境模板本地为空"警告文案(选项已过滤,警告是冗余信息)
            Assert.Null(vm.TemplateWarningMessage);
            // CanConfirm=false:TemplateOptions 空 + 选中的 ComfyUI 不在 options 里
            Assert.False(vm.CanConfirm);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_HidesTemplateKind_WhenLocalDirMissing_AmongMultiple()
    {
        // v1.0.0.x:TemplateOptions = Settings.Templates 中 LocalDirExists=true 的子集。
        // ComfyUI 本地存在 + Forge 本地缺失 → TemplateOptions 只含 ComfyUI。
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var settings = MakeSettings("3.10", "python", interpreters, activeName);
            settings.Templates["Forge"] = new TemplateConfig
            {
                Kind = "Forge", LocalSourceDir = "Templates/Forge",
                EntryScript = "webui.py", EntryArgs = "--port {port}",
                ModelsSubdir = "models/Stable-diffusion",
            };
            // Forge 在 SystemTemplateLibraryDir 下没目录 → LocalDirExists=false
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            Assert.Single(vm.TemplateOptions);
            Assert.Equal("ComfyUI", vm.TemplateOptions[0].Kind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CanConfirm_False_WhenTemplateOptionsEmpty()
    {
        // v1.0.0.x:所有 template 都被过滤掉 → TemplateOptions 空 → CanConfirm=false
        // (用户必须先在 TemplateManagement 下载模板才能建 env)
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var settings = MakeSettings("3.10", "python", interpreters, activeName);
            settings.SystemTemplateLibraryDir = Path.Combine(Path.GetTempPath(), "no-such-anchor-" + Guid.NewGuid().ToString("N")[..8]);
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            vm.Name = "x";
            // TemplateOptions 空 + PythonExe 已就绪 + TemplateSource 已有(即便选项过滤,TemplateSource 文本还在)
            Assert.Empty(vm.TemplateOptions);
            Assert.False(vm.CanConfirm);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Constructor_FallsBackToFirstAvailable_WhenDefaultKindFilteredOut()
    {
        // v1.0.0.x:默认 SelectedTemplateKind="ComfyUI" 若 LocalDir 缺失,回退到
        // TemplateOptions 第一项(本例中唯一可用的 Forge)。
        var (root, py, _) = CreateTemplateTree("3.10");
        var (interpreters, activeName) = ActiveAt(py);
        try
        {
            var settings = MakeSettings("3.10", "python", interpreters, activeName);
            // 强制 ComfyUI 不在 SystemTemplateLibraryDir 下 → 过滤掉
            settings.SystemTemplateLibraryDir = Path.Combine(Path.GetTempPath(), "no-such-anchor-" + Guid.NewGuid().ToString("N")[..8]);
            // 新加 Forge + 让它的 LocalSourceDir 在 Settings.SystemTemplateLibraryDir 下存在
            var anchor = settings.SystemTemplateLibraryDir;
            var forgeDir = Path.Combine(anchor, "Templates", "Forge");
            Directory.CreateDirectory(forgeDir);
            Directory.CreateDirectory(Path.Combine(forgeDir, ".git"));
            settings.Templates["Forge"] = new TemplateConfig
            {
                Kind = "Forge", LocalSourceDir = "Templates/Forge",
                EntryScript = "webui.py", EntryArgs = "--port {port}",
                ModelsSubdir = "models/Stable-diffusion",
            };
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            // Forge 应是唯一 TemplateOptions + SelectedTemplateKind 应回退到 "Forge"
            Assert.Single(vm.TemplateOptions);
            Assert.Equal("Forge", vm.TemplateOptions[0].Kind);
            Assert.Equal("Forge", vm.SelectedTemplateKind);
            Assert.Equal("Templates/Forge", vm.TemplateSource);
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
            // v1.0.0.x: 强制 SystemTemplateLibraryDir 到「不存在」让 ComfyUI 模板本地检测为空
            var settings = MakeSettings("3.10");
            settings.SystemTemplateLibraryDir = Path.Combine(Path.GetTempPath(), "no-such-anchor-" + Guid.NewGuid().ToString("N")[..8]);
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            Assert.Equal("", vm.PythonExe);
            // v1.0.0.x: TemplateSource 现在始终填 LocalSourceDir,不再 blank。
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
            Assert.NotNull(vm.TemplateWarningMessage);
            Assert.Contains("请在设置页添加 Python 解释器", vm.TemplateWarningMessage);
            // v1.0.0.x: 旧的 "目标环境模板本地为空" 警告已废弃 — template 被过滤,不再追加该文案。
            Assert.DoesNotContain("目标环境模板本地为空", vm.TemplateWarningMessage);
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
            // v1.0.0 T7:TemplateSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
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
            // v1.0.0 T7:TemplateSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
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
            // v1.0.0 T7:TemplateSource 存 raw template LocalSourceDir("ComfyUITemplate")
            Assert.Equal("ComfyUITemplate", vm.TemplateSource);
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
            settings.Templates["Forge"] = new TemplateConfig
            {
                Kind = "Forge", LocalSourceDir = "Templates/Forge",
                EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
            };
            var vm = new CreateEnvDialogViewModel(null!, settings, root);
            vm.PythonExe = "C:\\user-overridden";
            vm.SelectedTemplateKind = "Forge";   // 切 template
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

        // 与 EnvCreatorService.CreateAsync emit 的 7 个 CreateStepReport 一一对应
        // v1.0.0.x: 「链接 ComfyUI 源」改名为「复制 template 源」(对齐 service emit),
        // 新增「链接 Models 目录」步骤;「保存配置」步骤删(YAML 不再写)。
        // v1.0.0.x:末尾新增「升级 venv 内 pip」对应 service step 6.5(所有模板都跑)。
        Assert.Equal(7, vm.Steps.Count);
        Assert.Equal("校验输入", vm.Steps[0].Name);
        Assert.Equal("分配端口", vm.Steps[1].Name);
        Assert.Equal("创建 env 根目录", vm.Steps[2].Name);
        Assert.Equal("复制 template 源", vm.Steps[3].Name);
        Assert.Equal("链接 Models 目录", vm.Steps[4].Name);
        Assert.Equal("创建 venv 环境", vm.Steps[5].Name);
        Assert.Equal("升级 venv 内 pip", vm.Steps[6].Name);
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
        // v1.0.0.x: env-create 步骤列表从 6 项改为 7 项(改名 + 加项):
        //   1.校验输入 → 2.分配端口 → 3.创建 env 根目录 → 4.复制 template 源
        //   → 5.链接 Models 目录 → 6.创建 venv 环境 → 7.升级 venv 内 pip
        // 「保存配置」步骤删(YAML 写不再有);「链接 ComfyUI 源」改名为
        // 「复制 template 源」;新增「链接 Models 目录」对齐 service 实际进度;
        // 末尾新增「升级 venv 内 pip」对应 service step 6.5。
        var (vm, _, _, _, _, _) = MakeVm();

        Assert.Equal(7, vm.Steps.Count);
        Assert.Equal("校验输入", vm.Steps[0].Name);
        Assert.Equal("分配端口", vm.Steps[1].Name);
        Assert.Equal("创建 env 根目录", vm.Steps[2].Name);
        Assert.Equal("复制 template 源", vm.Steps[3].Name);
        Assert.Equal("链接 Models 目录", vm.Steps[4].Name);
        Assert.Equal("创建 venv 环境", vm.Steps[5].Name);
        Assert.Equal("升级 venv 内 pip", vm.Steps[6].Name);

        // 一次性推到第 7 个 step
        for (int i = 0; i < vm.Steps.Count - 1; i++)
        {
            vm.OnStepReport(new CreateStepReport(vm.Steps[i].Name));
        }
        vm.OnStepReport(new CreateStepReport("升级 venv 内 pip", "/venv/Scripts/python.exe -m pip install --upgrade pip"));

        Assert.Equal(CreateStepStatus.Done, vm.Steps[0].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[1].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[2].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[3].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[4].Status);
        Assert.Equal(CreateStepStatus.Done, vm.Steps[5].Status);
        Assert.Equal(CreateStepStatus.Running, vm.Steps[6].Status);
        Assert.Equal("/venv/Scripts/python.exe -m pip install --upgrade pip", vm.Steps[6].Detail);
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