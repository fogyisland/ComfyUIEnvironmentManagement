using System;
using System.IO;
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
/// v0.6.11+ SDD D1 T3: <see cref="MainViewModel.RestartEnvAsync"/> is the entry
/// point that InstallDialog calls after a successful node install (T1). T2 wired
/// the MVM reverse reference (SetMainViewModel) so this method can reach
/// <see cref="EnvironmentListViewModel.RestartEnvInternalAsync"/>. T3 must:
/// - Find env by id in EnvListVM.Environments; if not found → log warn + return
///   (AppLogger nullable; safe when null).
/// - If EnvListVM is null (ShowEnvironments never called) → log warn + return.
/// - Switch to env-list tab via <see cref="MainViewModel.ShowEnvironmentsCommand"/>
///   BEFORE calling RestartEnvInternalAsync so user sees the progress panel.
/// - Use <c>RestartEnvOverride</c> test seam (Func&lt;string, Task&gt;?) when set;
///   this lets unit tests bypass real EnvListVM.RestartEnvInternalAsync to avoid
///   ProcessLauncher / STA side effects.
/// - Never rethrow on EnvListVM restart failure (RestartEnvInternalAsync catches
///   internally + status.Fail + AppLogger.Error).
/// </summary>
public sealed class MainViewModelRestartEnvTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelRestartEnvTests()
    {
        _projectRoot = Path.Combine(
            Path.GetTempPath(),
            "mvm-restart-env-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewMvm()
    {
        // 21 必填 + 4 optional null;UiPreferencesService 必须非 null(VM ctor 抛 ANE);
        // 24th param(ComfyUIManagerInstaller)T3 仍 nullable → null;
        // 25th(AppLogger)T3 新加 — nullable → null 测试 logger 安全路径。
        return new MainViewModel(
            _db.Factory,
            null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot),
            baseEnvUninstaller: null, requirementsUninstaller: null,
            themeService: null, dashboardService: null,
            globalSearchService: null, browserLauncher: null,
            comfyUiManagerInstaller: null, logger: null);
    }

    [Fact]
    public async Task RestartEnvAsync_EnvNotFound_LogsWarn_NoCrash()
    {
        // EnvListVM has no envs → envId lookup misses → log warn + return.
        // We don't pass a logger (null) — _logger?.Warn safe when null, no NRE.
        // Seed env with a different id so lookup misses.
        SeedEnv("env-in-list", "stopped");
        var (mvm, _) = NewMvmWithRealEnvListVm();

        // Should complete without throwing.
        await mvm.RestartEnvAsync("missing-id");
    }

    [Fact]
    public async Task RestartEnvAsync_EnvFound_InvokesEnvListRestartInternal()
    {
        // Real EnvListVM: env seeded into the DB BEFORE MVM creation so Load()
        // picks it up → MVM finds it by Id → calls RestartEnvInternalAsync →
        // StartEnvForTest captures the call.
        var env = SeedEnv("env-1", "stopped");
        var (mvm, envList) = NewMvmWithRealEnvListVm();
        var startCalled = false;
        envList.StartEnvForTest = (_, _, _, _) => { startCalled = true; return Task.CompletedTask; };

        await mvm.RestartEnvAsync("env-1");

        Assert.True(startCalled, "EnvListVM.RestartEnvInternalAsync should have invoked StartEnvForTest");
        _ = env;
    }

    [Fact]
    public void RestartEnvAsync_NavigatesToEnvironmentListTab()
    {
        // User is on Catalog tab → restart should switch CurrentSection back to
        // Environments so the inline status panel is visible.
        var (mvm, _) = NewMvmWithRealEnvListVm();

        // ShowCatalogCommand is OK in real flow but here we lack catalog services
        // (TestDb has empty catalog tables). Wrap in try/catch so the test only
        // asserts on CurrentSection. (Same pattern as MainViewModelNavigationTests
        // ExecuteAllowingViewConstructionFailure.)
        try { mvm.ShowCatalogCommand.Execute(null); } catch { /* catalog deps null in MVM test ctor */ }
        Assert.Equal(MainSection.Catalog, mvm.CurrentSection);

        // 不 await — 我们只验 CurrentSection 切到了 Environments(切 tab 是 sync)。
        // The restart itself will hit a non-existent envId, log warn + return.
        _ = mvm.RestartEnvAsync("missing-id");

        Assert.Equal(MainSection.Environments, mvm.CurrentSection);
    }

    [Fact]
    public async Task RestartEnvAsync_RestartEnvOverride_UsedInstead()
    {
        // Test seam intercepts BEFORE the real EnvListVM.RestartEnvInternalAsync;
        // the captured envId is what the test asserts, and StartEnvForTest counter
        // (on real EnvListVM) must remain at 0 (override short-circuits).
        SeedEnv("env-1", "stopped");
        var (mvm, envList) = NewMvmWithRealEnvListVm();
        var startCalled = false;
        envList.StartEnvForTest = (_, _, _, _) => { startCalled = true; return Task.CompletedTask; };

        string? capturedEnvId = null;
        mvm.RestartEnvOverride = id =>
        {
            capturedEnvId = id;
            return Task.CompletedTask;
        };

        await mvm.RestartEnvAsync("env-1");

        Assert.Equal("env-1", capturedEnvId);
        Assert.False(startCalled);
    }

    [Fact]
    public async Task RestartEnvAsync_LogsError_PropagatesNothing()
    {
        // Real EnvListVM.RestartEnvInternalAsync catches internally (status.Fail +
        // AppLogger.Error) — MVM's await must therefore complete normally even
        // when the inner StartEnvForTest throws. This mirrors the T2 contract
        // verified in EnvironmentListViewModelRestartTests.
        SeedEnv("env-1", "stopped");
        var (mvm, envList) = NewMvmWithRealEnvListVm();
        envList.StartEnvForTest = (_, _, _, _) => throw new InvalidOperationException("boom");

        // Does not throw — exception is captured inside EnvListVM.
        await mvm.RestartEnvAsync("env-1");
    }

    // ---- helpers ----

    /// <summary>
    /// Real EnvListViewModel wired with the test's <see cref="TestDb"/> repo and
    /// the test's <c>_projectRoot</c>. All launcher / installer deps are null! —
    /// the test seam <c>StartEnvForTest</c> / <c>StopEnvForTest</c> intercept
    /// ProcessLauncher calls so the rest of EnvListVM never dereferences them.
    /// </summary>
    private (MainViewModel mvm, EnvironmentListViewModel envListVm) NewMvmWithRealEnvListVm()
    {
        var mvm = NewMvm();
        var envList = new EnvironmentListViewModel(
            new EnvironmentRepository(_db.Factory),
            null!, null!, null!, null!, null!, null!, null!,
            _projectRoot, null!,
            baseEnvUninstaller: null,
            requirementsUninstaller: null,
            browserLauncher: null,
            errorBanner: null,
            comfyUiManagerInstaller: null,
            logger: null);
        SetEnvListVm(mvm, envList);
        return (mvm, envList);
    }

    /// <summary>
    /// Insert an env row directly via repo so EnvListVM.Load() picks it up.
    /// </summary>
    private Environment SeedEnv(string id, string status)
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_projectRoot, id),
            ComfyuiLayout = "isolated",
            Status = status,
        };
        Directory.CreateDirectory(env.RootPath);
        new EnvironmentRepository(_db.Factory).Upsert(env);
        return env;
    }

    /// <summary>
    /// Reflection helper — MVM's <c>_environmentsViewModel</c> field is private and
    /// only <c>ShowEnvironments</c> populates it; tests skip the WPF STA view
    /// construction and inject a real EnvListVM directly. The injected EnvListVM's
    /// <c>Load()</c> has already run (in its ctor) and populated Environments.
    /// </summary>
    private static void SetEnvListVm(MainViewModel mvm, EnvironmentListViewModel envList)
    {
        var field = typeof(MainViewModel).GetField(
            "_environmentsViewModel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_environmentsViewModel field not found");
        field.SetValue(mvm, envList);
    }
}
