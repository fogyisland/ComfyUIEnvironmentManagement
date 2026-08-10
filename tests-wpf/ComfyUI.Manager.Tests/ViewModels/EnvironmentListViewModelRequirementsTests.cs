using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.5.12: env-list 操作列加 6th 按钮 "装依赖"。v0.6.5.15 改成 inline 状态面板后,
/// 测试改验 RequirementsStatus 属性而不是 dialog override seam。
/// </summary>
public sealed class EnvironmentListViewModelRequirementsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelRequirementsTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(), $"envlistvm-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status = "stopped")
    {
        var venv = Path.Combine(_tempRoot, id, "venv");
        Directory.CreateDirectory(venv);
        File.WriteAllText(Path.Combine(venv, "fake-python.exe"), "");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tempRoot, id),
            VenvPath = venv,
            PythonExecutable = Path.Combine(venv, "fake-python.exe"),
            ComfyuiLayout = "isolated",
            CustomNodesPath = Path.Combine(_tempRoot, id, "nodes"),
            Port = 8188,
            Status = status,
            BedStatus = "done",
        };
        Directory.CreateDirectory(env.RootPath);
        Directory.CreateDirectory(env.CustomNodesPath);
        File.WriteAllText(Path.Combine(env.RootPath, "requirements.txt"), "SQLAlchemy");
        _repo.Upsert(env);
        return env;
    }

    private EnvironmentListViewModel NewVm(RequirementsInstaller? installer = null)
    {
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot, installer ?? new RequirementsInstaller());
    }

    /// <summary>
    /// 假 installer:不真跑 pip,返指定 PipResult,可推 stdout 行到 IProgress。
    /// v0.6.11+ T1:RequirementsInstaller 不再暴露 RunPipAsync 给子类覆盖,pip 跑到
    /// RequirementsFileInstaller.InstallAsync 内部。fake 改 override InstallAsync。
    /// </summary>
    private sealed class FakeInstaller : RequirementsInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public List<string> EmittedLines { get; } = new();
        public int CallCount { get; private set; }

        public override async Task<RequirementsInstallResult> InstallAsync(
            Environment env,
            IProgress<string>? logProgress,
            CancellationToken ct)
        {
            // 模拟 RequirementsFileInstaller 内部行:写 filtered → 推 stdout 行 → 返指定 PipResult
            var candidates = ResolveRequirementsCandidates(env);
            var reqPath = candidates.FirstOrDefault(File.Exists);
            if (reqPath is null)
                return new RequirementsInstallResult(false, false, "找不到 requirements.txt", 0);

            var rawLines = await File.ReadAllLinesAsync(reqPath, ct);
            var filtered = FilterTorchLines(rawLines);
            var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
            await File.WriteAllLinesAsync(filteredPath, filtered, ct);

            CallCount++;
            logProgress?.Report("Looking in indexes: https://pypi.org/simple");
            logProgress?.Report("Collecting SQLAlchemy");
            EmittedLines.AddRange(EmittedLines);
            try { File.Delete(filteredPath); } catch { }

            if (NextResult.WasCancelled)
                return new RequirementsInstallResult(false, true, "用户取消", 0);
            if (NextResult.ExitCode != 0)
                return new RequirementsInstallResult(false, false, $"pip 退出码 {NextResult.ExitCode}", 0);

            File.WriteAllText(Path.Combine(env.RootPath, MarkerFileName),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            return new RequirementsInstallResult(true, false, null, filtered.Count);
        }
    }

    [Fact]
    public void InstallRequirementsCommand_EnabledForAnyEnv()
    {
        SeedEnv("env-a");
        var vm = NewVm();
        Assert.True(vm.InstallRequirementsCommand.CanExecute(vm.Environments[0]));
    }

    [Fact]
    public void InstallRequirementsCommand_DisabledWhenNoEnvSelected()
    {
        var vm = NewVm();
        Assert.False(vm.InstallRequirementsCommand.CanExecute(null));
    }

    [Fact]
    public void InstallRequirementsCommand_NullEnv_DoesNothing()
    {
        var vm = NewVm();
        vm.InstallRequirementsCommand.Execute(null);
        Assert.Null(vm.RequirementsStatus);
    }

    [Fact]
    public async Task InstallRequirementsCommand_CreatesRequirementsStatusWithCorrectEnv()
    {
        var env = SeedEnv("env-a");
        var fake = new FakeInstaller { NextResult = new PipResult(0, false) };
        var vm = NewVm(fake);

        await InvokeAsync(vm, env);

        Assert.NotNull(vm.RequirementsStatus);
        Assert.Equal("env-a", vm.RequirementsStatus!.EnvName);
        Assert.True(vm.RequirementsStatus.IsVisible);
    }

    [Fact]
    public async Task InstallRequirementsCommand_Succeeds_MarksStatusCompleteWithoutError()
    {
        var env = SeedEnv("env-a");
        var fake = new FakeInstaller { NextResult = new PipResult(0, false) };
        var vm = NewVm(fake);

        await InvokeAsync(vm, env);

        var status = vm.RequirementsStatus!;
        Assert.True(status.IsComplete);
        Assert.False(status.HasError);
        Assert.Contains("装依赖完成", status.StatusText);
    }

    [Fact]
    public async Task InstallRequirementsCommand_Fails_KeepsStatusVisibleWithError()
    {
        var env = SeedEnv("env-a");
        var fake = new FakeInstaller { NextResult = new PipResult(1, false) };
        var vm = NewVm(fake);

        await InvokeAsync(vm, env);

        var status = vm.RequirementsStatus!;
        Assert.True(status.IsComplete);
        Assert.True(status.HasError);
        Assert.Contains("退出码 1", status.Error);
        Assert.True(status.IsVisible);  // 失败后面板保持可见,等用户手动关
    }

    [Fact]
    public void InstallRequirementsCommand_RaiseCanExecute_StillExecutable()
    {
        SeedEnv("env-a");
        var vm = NewVm();
        vm.InstallRequirementsCommand.RaiseCanExecuteChanged();
        Assert.True(vm.InstallRequirementsCommand.CanExecute(vm.Environments[0]));
    }

    [Fact]
    public async Task InstallRequirementsCommand_AlreadyInstalled_ShortCircuitsAndDoesNotRunPip()
    {
        // v0.6.5.19 hotfix: 已装过(marker 文件存在)再次点击应显示已安装状态,
        // 不重复跑 pip — pip install -r 幂等但慢,且混淆"装过没装过"的判断。
        var env = SeedEnv("env-a");
        var markerPath = Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName);
        File.WriteAllText(markerPath, "2026-08-05T19:50:00Z");
        var fake = new FakeInstaller();
        var vm = NewVm(fake);

        await InvokeAsync(vm, env);

        var status = vm.RequirementsStatus!;
        Assert.True(status.IsComplete);
        Assert.False(status.HasError);
        Assert.Contains("已安装依赖", status.StatusText);
        Assert.Contains("2026-08-05T19:50:00Z", status.StatusText);
        Assert.Equal(0, fake.CallCount);  // 关键:pip 没跑
    }

    [Fact]
    public async Task InstallRequirementsCommand_AlreadyInstalled_MarkerEmpty_FallsBackToUnknown()
    {
        // marker 文件存在但内容为空 — UI 仍显示已安装,timestamp 回退 "未知"
        var env = SeedEnv("env-a");
        var markerPath = Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName);
        File.WriteAllText(markerPath, "");
        var fake = new FakeInstaller();
        var vm = NewVm(fake);

        await InvokeAsync(vm, env);

        var status = vm.RequirementsStatus!;
        Assert.True(status.IsComplete);
        Assert.Contains("已安装", status.StatusText);
        Assert.Contains("未知", status.StatusText);
        Assert.Equal(0, fake.CallCount);
    }

    /// <summary>
    /// 把 RelayCommand.Execute 包的同步调用转成 await — InstallRequirementsCommand
    /// 内部是 async,RelayCommand.Execute 不 await,所以我们需要手动等。
    /// </summary>
    private static async Task InvokeAsync(EnvironmentListViewModel vm, Environment env)
    {
        vm.InstallRequirementsCommand.Execute(env);
        // 等 RequirementsStatus 进入终态(IsComplete 或 HasError 都算)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.RequirementsStatus is null || !vm.RequirementsStatus.IsComplete)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("RequirementsStatus did not complete in time");
            await Task.Delay(20);
        }
    }
}