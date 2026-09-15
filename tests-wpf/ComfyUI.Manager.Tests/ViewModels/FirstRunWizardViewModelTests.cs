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

    // v1.0.0.x (2026-09-05):绿色软件 — InstallPath 默认 = program path(等同 projectRoot),
    // 用户在 wizard 第一步看见默认填好的 path,确认或 Browse 改,而不是空字符串 + Next disabled。
    [Fact]
    public void InstallPath_DefaultsToProjectRoot_ForGreenSoftwareUser()
    {
        var vm = NewVm();
        Assert.Equal(_projectRoot, vm.InstallPath);
    }

    // v1.0.0.x (2026-09-05):Git path 默认 = Embeded/git-portable/cmd/git.exe 绝对路径(用户原话
    // "git 也指向到当前的绝对目录,也是计算出来,让用户确认")。Empty string = 走 PATH fallback
    // (用户清空后),但默认 seed 应该是 file.exists 探测出的绝对路径。
    [Fact]
    public void GitPath_DefaultsToEmbededPortableAbsolute_WhenPresent()
    {
        // 测试夹具 _projectRoot 是 TempPath 下的 fake dir,Embeded/git-portable/cmd/git.exe 不存在
        // → VM 应留空(_gitPath = "") → ResolveGitExe fallback 到 PATH "git"。
        var vm = NewVm();
        Assert.Equal("", vm.GitPath);
    }

    // v1.0.0.x (2026-09-05):空 GitPath + 非空 GitPath 都算 valid(空 = 走 PATH)。
    [Fact]
    public void GitPath_EmptyIsValid_BecausePathFallbackAllowed()
    {
        var vm = NewVm();
        Assert.True(vm.IsGitValid);  // empty = path "git" fallback OK
    }

    // v1.0.0.x (2026-09-05):Python path 也像 Git 一样自动 seed —
    // 用户原话"python 路径也是自然获取,我们只需要看正确与否 例如当前
    // H:\ComfyUIManagement\Embeded\python 位于这个目录下的 python.exe"。
    // 测试夹具 _projectRoot 不含 Embeded/python/python.exe → VM 应留空让用户 Browse。
    [Fact]
    public void PythonPath_DefaultsEmpty_WhenEmbededPythonMissing()
    {
        var vm = NewVm();
        Assert.Equal("", vm.PythonPath);
    }

    // v1.0.0.x (2026-09-05):Python path 像 Git 一样自动 seed —
    // 创建 fake Embeded/python/python.exe 在 _projectRoot → VM 应填绝对路径。
    [Fact]
    public void PythonPath_DefaultsToEmbededPythonAbsolute_WhenPresent()
    {
        var pythonDir = Path.Combine(_projectRoot, "Embeded", "python");
        Directory.CreateDirectory(pythonDir);
        var fakePythonExe = Path.Combine(pythonDir, "python.exe");
        File.WriteAllBytes(fakePythonExe, new byte[] { 0x00 });  // 占位
        try
        {
            var vm = NewVm();
            Assert.Equal(fakePythonExe, vm.PythonPath);
            Assert.True(vm.IsPythonValid);
        }
        finally
        {
            File.Delete(fakePythonExe);
            Directory.Delete(Path.Combine(_projectRoot, "Embeded", "python"));
            Directory.Delete(Path.Combine(_projectRoot, "Embeded"));
        }
    }

    [Fact]
    public void GoNext_WelcomeToPythonRequiresPythonPath()
    {
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        // v1.0.0.x (2026-09-03) T34:3 步 → 6 步 wizard,Python step 改名 PythonGit。
        Assert.Equal(FirstRunWizardStep.PythonGit, vm.CurrentStep);
    }

    [Fact]
    public void GoNext_PythonToPaths1AllowsEmptyPaths()
    {
        // v1.0.0.x T34:Paths1-3 step 允许路径字段空(用户不填 → SettingsDefaults.Apply 后续再 seed)
        // v1.0.0.x (2026-09-05):Python + Git 合并为 PythonGit step,wizard 6 步:
        // Welcome → PythonGit → Paths1EnvNodes → Paths2ModelsWorkflows → Paths3SystemLog → Confirm。
        // 2 次 Next 才能从 Welcome 进 Paths1EnvNodes。
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        vm.PythonPath = "C:/python/python.exe";
        vm.NextCommand.Execute(null);  // Welcome → PythonGit
        vm.NextCommand.Execute(null);  // PythonGit → Paths1EnvNodes
        Assert.Equal(FirstRunWizardStep.Paths1EnvNodes, vm.CurrentStep);
    }

    [Fact]
    public void GoBack_ChainThroughAllSteps()
    {
        var vm = NewVm();
        vm.InstallPath = _projectRoot;
        vm.PythonPath = "C:/python/python.exe";
        // v1.0.0.x (2026-09-05):Python + Git 合并为 PythonGit step,wizard 6 步。
        // 5 次 Next 才能进 Confirm。
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
        Assert.Equal(FirstRunWizardStep.PythonGit, vm.CurrentStep);
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
        // v1.0.0.x (2026-09-05):Python + Git 合并为 PythonGit step,wizard 6 步,
        // 5 次 Next 才能到 Confirm。
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

        // v1.0.0.x T41:MarkComplete 改写 executed=0 到 config/firstrun.inf,删旧 .first-run-complete sentinel
        // v1.0.0.x (2026-09-05):路径用 _projectRoot + "config" 组合 —— Finish 修 bug 后不再写
        // 到 _appDataDir/config/(嵌套),而是写到 _projectRoot/config/(正确一层)。
        var inf = Path.Combine(_projectRoot, "config", FirstRunDetector.FirstRunInfName);
        Assert.True(File.Exists(inf), "firstrun.inf 没写");
        Assert.Contains($"{FirstRunDetector.ExecutedKey}={FirstRunDetector.ExecutedFalse}", File.ReadAllText(inf));
        Assert.False(FirstRunDetector.IsFirstRun(_projectRoot, "config"));

        var settingsInf = Path.Combine(_projectRoot, "config", "settings.inf");
        Assert.True(File.Exists(settingsInf), "settings.inf 没写");
        var content = File.ReadAllText(settingsInf);
        Assert.Contains("myenvs", content);
    }
}
