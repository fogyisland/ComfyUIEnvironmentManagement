using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests;
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
            null!,   // baseEnvUninstaller
            null!,   // requirementsUninstaller
            null!,   // browserLauncher
            null!,   // errorBanner
            null!,   // comfyUiManagerInstaller
            null!,   // logger
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            new NodeRepository(_db.Factory),
            new NodeVersionRepository(new CatalogCacheStore(_db.Path)));
    }

    /// <summary>
    /// v0.6.11+ SDD D1 R1:Build a minimal MainViewModel for callback-injection tests.
    /// Most deps are null! — we only exercise the RestartEnvAsync virtual stub which
    /// doesn't touch any field. Mirrors MainViewModelNavigationTests.NewVm pattern.
    /// </summary>
    private MainViewModel NewMvm()
    {
        return new MainViewModel(
            _db.Factory,
            null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!,
            null!, "", _tempRoot, null!, null!, new UiPreferencesService(_tempRoot),
            baseEnvUninstaller: null, requirementsUninstaller: null,
            themeService: null, dashboardService: null);
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

        // R1:验 status panel 真的显了用户可见失败(不只静默 catch)。
        // EnvStartStatusViewModel.Fail(reason) 设 status.Error,所以直接 Assert。
        var status = vm.StartStatus;
        Assert.NotNull(status);
        Assert.NotNull(status!.Error);
        Assert.Contains("失败", status.Error, StringComparison.Ordinal);
        Assert.Contains("boom", status.Error, StringComparison.Ordinal);

        // 第二次再调:busy 应已清,能正常 Start
        env.Status = "running";
        vm.StartEnvForTest = (_, _, _, _) => Task.CompletedTask;
        await vm.RestartEnvInternalAsync(env, CancellationToken.None);
    }

    /// <summary>
    /// v0.6.14 T5:verify OpenInstallNodePicker 把 _mvm.RestartEnvAsync 当
    /// onInstallSuccess 回调注入 CatalogEntryPickerDialog.Show(v0.6.11 走的是
    /// InstallDialog.Show,T5 改走 picker 行内安装 — picker 接管回调)。
    /// 这是 §4 spec 的核心 wiring —— reviewer 反馈 T2 commit `1be68df` 没接 MVM
    /// wiring,EnvListVM._mvm 永远 null,装成功不触发重启。这个测试保证:(a)
    /// SetMainViewModel 真正接到 _mvm,(b) OpenInstallNodePicker 实际把
    /// _mvm.RestartEnvAsync 当回调传给 picker。
    /// delegate equality 用 System.Delegate.op_Equality — 同一个 instance 的同 method 必等。
    /// </summary>
    [Fact]
    public void OpenInstallNodePicker_InjectsRestartCallback_WhenMvmSet()
    {
        var env = SeedEnv("env-cb", "stopped");
        var vm = NewVm();
        var mvm = NewMvm();

        // (a) 验证 SetMainViewModel 注入
        vm.SetMainViewModel(mvm);

        // 拦截 CatalogEntryPickerDialog.Show,捕获 (envId, onInstallSuccess) 实际传入
        string? capturedEnvId = null;
        Func<string, Task>? capturedCallback = null;
        global::ComfyUI.Manager.Views.CatalogEntryPickerDialog.ShowOverride =
            (_, _, _, _, _, _, envId, onSuccess, _) =>
            {
                capturedEnvId = envId;
                capturedCallback = onSuccess;
                return null;
            };

        try
        {
            // 触发 env-list 行 "安装节点" 命令(RelayCommand fire-and-forget 但同步构造完路径)
            vm.InstallNodeCommand.Execute(env);

            // (b) 验证 callback 注入:env.Id 跟 mvm.RestartEnvAsync 一致
            Assert.Equal("env-cb", capturedEnvId);
            Assert.NotNull(capturedCallback);
            // 注:不直接 Assert.Same — C# method-group conversion 在某些版本下会缓存,
            // 但更稳的做法是比 Method+Target — 同一个 instance + 同 method 必命中。
            Assert.Same(mvm, capturedCallback!.Target);
            Assert.Equal(nameof(MainViewModel.RestartEnvAsync), capturedCallback.Method.Name);
        }
        finally
        {
            global::ComfyUI.Manager.Views.CatalogEntryPickerDialog.ShowOverride = null;
        }
    }

    /// <summary>
    /// v0.6.14 T5:验证 OpenInstallNodePicker 在 _mvm == null 时(EnvListVM
    /// 早于 MVM 构造,如测试直接构造 EnvListVM)不传回调 — 行为跟 v0.6.11 既有兼容。
    /// 这是 reviewer 担心的另一面:如果没 _mvm 时传 null callback 是 OK 的,
    /// 证明 wiring 是 _mvm-aware 而不是无脑 always pass。
    /// </summary>
    [Fact]
    public void OpenInstallNodePicker_NullCallback_WhenMvmNotSet()
    {
        var env = SeedEnv("env-cb-null", "stopped");
        var vm = NewVm();  // 注意:没调 SetMainViewModel → _mvm 仍 null

        Func<string, Task>? capturedCallback = _ => Task.CompletedTask;
        global::ComfyUI.Manager.Views.CatalogEntryPickerDialog.ShowOverride =
            (_, _, _, _, _, _, envId, onSuccess, _) =>
            {
                capturedCallback = onSuccess;
                return null;
            };

        try
        {
            vm.InstallNodeCommand.Execute(env);

            Assert.Null(capturedCallback);
        }
        finally
        {
            global::ComfyUI.Manager.Views.CatalogEntryPickerDialog.ShowOverride = null;
        }
    }
}
