using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services.FirstRun;
using ComfyUI.Manager.ViewModels.FirstRunWizard;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-09-03) T34:锁 FirstRunWizardViewModel 行为 ——
/// - 默认 seed 8 个 path 字段是 <c>&lt;projectRoot&gt;/&lt;Kind&gt;/</c> 模式(镜像 SettingsDefaults.Apply)
/// - GoNext / GoBack step 跳转
/// - Finish 写 settings via SettingsRepository.Save + sentinel
/// </summary>
public class FirstRunWizardViewModelTests : IDisposable
{
    private readonly string _appDataDir;
    private readonly string _projectRoot;

    public FirstRunWizardViewModelTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "first-run-wizard-test-" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(Path.GetTempPath(), "first-run-wizard-proj-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDataDir, recursive: true); } catch { }
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private FirstRunWizardViewModel NewVm() => new FirstRunWizardViewModel(_appDataDir, _projectRoot);

    [Fact]
    public void DefaultSeedPaths_FollowProjectRootKindPattern()
    {
        var vm = NewVm();
        Assert.Equal(Path.Combine(_projectRoot, "ENVTemplate") + Path.DirectorySeparatorChar, vm.SystemTemplateLibraryDir);
        Assert.Equal(Path.Combine(_projectRoot, "Envs") + Path.DirectorySeparatorChar, vm.EnvsDir);
        Assert.Equal(Path.Combine(_projectRoot, "Nodes") + Path.DirectorySeparatorChar, vm.GlobalNodesDir);
        Assert.Equal(Path.Combine(_projectRoot, "LocalNodes") + Path.DirectorySeparatorChar, vm.LocalNodeDirectory);
        Assert.Equal(Path.Combine(_projectRoot, "localnodes") + Path.DirectorySeparatorChar, vm.LocalNodesDirectory);
        Assert.Equal(Path.Combine(_projectRoot, "Models") + Path.DirectorySeparatorChar, vm.DefaultModelsDirectory);
        Assert.Equal(Path.Combine(_projectRoot, "Workflow") + Path.DirectorySeparatorChar, vm.WorkflowsDirectory);
        Assert.Equal(Path.Combine(_projectRoot, "logs") + Path.DirectorySeparatorChar, vm.LogDirectory);
    }

    [Fact]
    public void GoNext_WelcomeToPythonRequiresPythonPath()
    {
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Python, vm.CurrentStep);
    }

    [Fact]
    public void GoNext_PythonToPaths1AllowsEmptyPaths()
    {
        // v1.0.0.x T34:Paths1-3 step 允许路径字段空(用户不填 → SettingsDefaults.Apply 后续再 seed)
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        vm.PythonPath = "C:/python/python.exe";
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Paths1EnvNodes, vm.CurrentStep);
    }

    [Fact]
    public void GoBack_ChainThroughAllSteps()
    {
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        vm.PythonPath = "C:/python/python.exe";
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Confirm, vm.CurrentStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Paths3SystemLog, vm.CurrentStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Paths2ModelsWorkflows, vm.CurrentStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Paths1EnvNodes, vm.CurrentStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Python, vm.CurrentStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void Finish_WritesSettingsInfAndMarksFirstRunComplete()
    {
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        vm.PythonPath = "C:/python/python.exe";
        vm.EnvsDir = Path.Combine(_projectRoot, "myenvs") + Path.DirectorySeparatorChar;
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal(FirstRunWizardStep.Confirm, vm.CurrentStep);

        bool completed = false;
        vm.Completed += () => completed = true;
        vm.FinishCommand.Execute(null);
        Assert.True(completed);

        var sentinel = Path.Combine(_appDataDir, FirstRunDetector.SentinelFileName);
        Assert.True(File.Exists(sentinel), "Sentinel file 没写");
        Assert.False(FirstRunDetector.IsFirstRun(_appDataDir));

        var settingsInf = Path.Combine(_appDataDir, "config", "settings.inf");
        Assert.True(File.Exists(settingsInf), "settings.inf 没写");
        var content = File.ReadAllText(settingsInf);
        Assert.Contains("myenvs", content);
    }
}
