using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// RequirementsStatusViewModel 测试(inline 模式取代之前的 dialog 模式)。
/// </summary>
public sealed class RequirementsStatusViewModelTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsStatusViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reqstatus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id = "env-a")
    {
        var venv = Path.Combine(_tempRoot, "venv");
        Directory.CreateDirectory(venv);
        File.WriteAllText(Path.Combine(venv, "fake-python.exe"), "");
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = _tempRoot,
            VenvPath = venv,
            PythonExecutable = Path.Combine(venv, "fake-python.exe"),
            CustomNodesPath = Path.Combine(_tempRoot, "nodes"),
            Port = 8188,
            Status = "stopped",
        };
    }

    private sealed class FakeInstaller : RequirementsInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public List<string> LogEmissions { get; } = new();

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            onLine("Looking in indexes: https://pypi.org/simple");
            onLine("Collecting SQLAlchemy");
            onLine("Installing collected packages: SQLAlchemy");
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public void Ctor_SetsEnvNameAndPendingStatus()
    {
        var env = SeedEnv();
        var vm = new RequirementsStatusViewModel(env, new FakeInstaller());
        Assert.Equal("env-a", vm.EnvName);
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsComplete);
        Assert.Contains("准备", vm.StatusText);
    }

    [Fact]
    public void CancelCommand_DisabledBeforeRun()
    {
        var vm = new RequirementsStatusViewModel(SeedEnv(), new FakeInstaller());
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunAsync_Succeeds_UpdatesStatusAndLogLines()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(0, false) };
        var vm = new RequirementsStatusViewModel(env, fake);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Contains("装依赖完成", vm.StatusText);
        Assert.NotEmpty(vm.LogLines);
        Assert.Contains(vm.LogLines, l => l.Contains("Looking in indexes"));
        Assert.Contains(vm.LogLines, l => l.Contains("Collecting SQLAlchemy"));
    }

    [Fact]
    public async Task RunAsync_Fails_SetsErrorAndStatusText()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(1, false) };
        var vm = new RequirementsStatusViewModel(env, fake);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.True(vm.HasError);
        Assert.Contains("退出码 1", vm.Error);
        Assert.Contains("退出码 1", vm.StatusText);
    }

    [Fact]
    public async Task RunAsync_NoRequirementsFile_FailsWithReason()
    {
        var env = SeedEnv();
        var fake = new FakeInstaller();
        var vm = new RequirementsStatusViewModel(env, fake);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.True(vm.HasError);
        Assert.Contains("找不到 ComfyUI 的 requirements.txt", vm.Error);
    }

    [Fact]
    public async Task RunAsync_IsVisibleTrueAfterStart()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var vm = new RequirementsStatusViewModel(env, new FakeInstaller());

        Assert.False(vm.IsVisible);
        await vm.RunAsync();
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public async Task Hide_ResetsState()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(1, false) };
        var vm = new RequirementsStatusViewModel(env, fake);

        await vm.RunAsync();  // 失败 → IsVisible=true, IsComplete=true, HasError=true
        Assert.True(vm.IsVisible);

        vm.Hide();
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Null(vm.Error);
        Assert.Empty(vm.LogLines);
    }

    [Fact]
    public async Task RunAsync_Cancelled_SetsError()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(130, WasCancelled: true) };
        var vm = new RequirementsStatusViewModel(env, fake);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.True(vm.HasError);
        Assert.Equal("用户取消", vm.Error);
    }

    [Fact]
    public void MarkAlreadyInstalled_SetsVisibleAndTimestamp()
    {
        // v0.6.5.19 hotfix: env-list 已装依赖后再点 → panel 直接显示已安装状态,
        // IsVisible=true, IsComplete=true, StatusText 含时间戳,不调 RunPipAsync
        // (FakeInstaller 不会被调,无错误抛出因为根本不走 RunAsync)。
        var env = SeedEnv();
        var vm = new RequirementsStatusViewModel(env, new FakeInstaller());

        vm.MarkAlreadyInstalled("2026-08-05T19:50:00Z");

        Assert.True(vm.IsVisible);
        Assert.True(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Contains("已安装依赖", vm.StatusText);
        Assert.Contains("2026-08-05T19:50:00Z", vm.StatusText);
    }
}