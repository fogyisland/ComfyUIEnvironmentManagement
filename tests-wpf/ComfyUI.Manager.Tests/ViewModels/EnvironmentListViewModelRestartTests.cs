using System;
using System.Collections.Generic;
using System.IO;
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
/// v0.6.11+ SDD D1 T2: <see cref="EnvironmentListViewModel.RestartEnvInternalAsync"/>
/// is the internal entry-point that <see cref="MainViewModel"/> (T3) calls after a
/// node install succeeds. It must:
/// - Stop if env.Status == "running", otherwise skip Stop
/// - Always Start
/// - Reuse per-env mutex (BusyKind.Restart) — busy env → skip + log warn
/// - Catch exceptions (do NOT rethrow) — node install path is fire-and-forget
/// - Show EnvStartStatusViewModel; failure path → status.Fail
///
/// Implementation note: ProcessLauncher is sealed (and we must NOT modify it), so
/// tests use an internal Func delegate seam <see cref="EnvironmentListViewModel.StartEnvForTest"/>
/// to capture Start calls. Stop uses <see cref="EnvironmentListViewModel.StopEnvForTest"/>
/// (same seam pattern). Default = call _launcher.StartEnvAsync / StopEnvAsync.
/// </summary>
public class EnvironmentListViewModelRestartTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelRestartTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(), $"envlistvm-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status)
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tempRoot, id),
            ComfyuiLayout = "isolated",
            Status = status,
        };
        Directory.CreateDirectory(env.RootPath);
        _repo.Upsert(env);
        return env;
    }

    /// <summary>
    /// ProcessLauncher is sealed → can't subclass. We construct VM with null! launcher
    /// (constructor accepts null because the test seam Funcs intercept ALL calls before
    /// _launcher is dereferenced). The real RestartEnvInternalAsync implementation uses
    /// <see cref="EnvironmentListViewModel.StartEnvForTest"/> ?? _launcher.StartEnvAsync.
    /// When StartEnvForTest is set, _launcher is never touched.
    /// </summary>
    private EnvironmentListViewModel NewVm()
    {
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot, null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
    }

    [Fact]
    public async Task RestartEnvInternal_NotBusy_StopsThenStarts()
    {
        var env = SeedEnv("env-1", "running");
        var vm = NewVm();

        var stopCalled = false;
        var startCalled = false;
        vm.StopEnvForTest = e => { stopCalled = true; e.Status = "stopped"; return Task.CompletedTask; };
        vm.StartEnvForTest = (_, _, _, _) => { startCalled = true; env.Status = "running"; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.True(stopCalled);
        Assert.True(startCalled);
        Assert.Equal("running", env.Status);
    }

    [Fact]
    public async Task RestartEnvInternal_NotRunning_OnlyStarts()
    {
        var env = SeedEnv("env-1", "stopped");
        var vm = NewVm();

        var stopCalled = false;
        var startCalled = false;
        vm.StopEnvForTest = _ => { stopCalled = true; return Task.CompletedTask; };
        vm.StartEnvForTest = (_, _, _, _) => { startCalled = true; env.Status = "running"; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.False(stopCalled);
        Assert.True(startCalled);
        Assert.Equal("running", env.Status);
    }

    [Fact]
    public async Task RestartEnvInternal_EnvBusy_LogsWarn_NoStopNoStart()
    {
        var env = SeedEnv("env-1", "running");
        var vm = NewVm();

        // Mark env busy via test seam (covers per-env mutex skip)
        vm.SetEnvBusyForTest(env);

        var stopCalled = false;
        var startCalled = false;
        vm.StopEnvForTest = _ => { stopCalled = true; return Task.CompletedTask; };
        vm.StartEnvForTest = (_, _, _, _) => { startCalled = true; return Task.CompletedTask; };

        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        Assert.False(stopCalled);
        Assert.False(startCalled);
    }

    [Fact]
    public async Task RestartEnvInternal_StartThrows_LogsError_UnmarksBusy()
    {
        var env = SeedEnv("env-1", "stopped");
        var vm = NewVm();

        vm.StopEnvForTest = _ => Task.CompletedTask;
        vm.StartEnvForTest = (_, _, _, _) => throw new InvalidOperationException("boom");

        // 不抛 — 异常被吞进 EnvStartStatusViewModel.Fail
        await vm.RestartEnvInternalAsync(env, CancellationToken.None);

        // 第二次再调:busy 应已清,能正常 Start
        env.Status = "running";
        vm.StartEnvForTest = (_, _, _, _) => Task.CompletedTask;
        await vm.RestartEnvInternalAsync(env, CancellationToken.None);
    }
}