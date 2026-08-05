using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class RequirementsProgressViewModelTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsProgressViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reqprogress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv()
    {
        var venv = Path.Combine(_tempRoot, "venv");
        Directory.CreateDirectory(venv);
        File.WriteAllText(Path.Combine(venv, "fake-python.exe"), "");
        return new Environment
        {
            Id = "env-a",
            Name = "env-a",
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
            // 模拟 pip 输出
            onLine("Looking in indexes: https://pypi.org/simple");
            onLine("Collecting SQLAlchemy");
            onLine("Installing collected packages: SQLAlchemy");
            LogEmissions.AddRange(LogEmissions);
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public void Ctor_SetsEnvNameAndPendingStatus()
    {
        var env = SeedEnv();
        var vm = new RequirementsProgressViewModel(env, new FakeInstaller());
        Assert.Equal("env-a", vm.EnvName);
        Assert.Equal(RequirementsInstallStatus.Pending, vm.OverallStatus);
        Assert.Contains("准备", vm.StatusText);
    }

    [Fact]
    public void CancelCommand_DisabledBeforeRun()
    {
        var vm = new RequirementsProgressViewModel(SeedEnv(), new FakeInstaller());
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunAsync_Succeeds_UpdatesOverallStatusAndLogTail()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(0, false) };
        var vm = new RequirementsProgressViewModel(env, fake);

        var result = await vm.RunAsync();
        vm.OnCompleted(result);

        Assert.True(result.Success);
        Assert.Equal(RequirementsInstallStatus.Succeeded, vm.OverallStatus);
        Assert.Contains("装依赖完成", vm.StatusText);
        Assert.Contains("Looking in indexes", vm.LogTail);
        Assert.Contains("Collecting SQLAlchemy", vm.LogTail);
    }

    [Fact]
    public async Task RunAsync_Fails_OverallStatusIsFailedWithReason()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(1, false) };
        var vm = new RequirementsProgressViewModel(env, fake);

        var result = await vm.RunAsync();
        vm.OnCompleted(result);

        Assert.False(result.Success);
        Assert.Equal(RequirementsInstallStatus.Failed, vm.OverallStatus);
        Assert.Contains("退出码 1", vm.StatusText);
    }

    [Fact]
    public async Task RunAsync_Cancelled_OverallStatusIsCancelled()
    {
        var env = SeedEnv();
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        var fake = new FakeInstaller { NextResult = new PipResult(130, WasCancelled: true) };
        var vm = new RequirementsProgressViewModel(env, fake);

        var result = await vm.RunAsync();
        vm.OnCompleted(result);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal(RequirementsInstallStatus.Cancelled, vm.OverallStatus);
    }

    [Fact]
    public void LogTail_CapsAtMaxLogLines()
    {
        var env = SeedEnv();
        var vm = new RequirementsProgressViewModel(env, new FakeInstaller());

        // 推 250 行
        for (int i = 0; i < 250; i++)
        {
            vm.OnLogLine($"line-{i}");
        }

        var lines = vm.LogTail.Split('\n');
        Assert.Equal(200, lines.Length);  // MaxLogLines
        // 最早 50 行被裁掉
        Assert.DoesNotContain("line-0", lines);
        Assert.Contains("line-249", lines);
    }
}
