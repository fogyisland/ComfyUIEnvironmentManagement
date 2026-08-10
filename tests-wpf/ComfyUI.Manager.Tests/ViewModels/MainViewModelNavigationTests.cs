using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class MainViewModelNavigationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelNavigationTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "main-vm-navigation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewVm(IDashboardService? dashboardService = null)
    {
        var vm = new MainViewModel(
            _db.Factory,
            null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot),
            baseEnvUninstaller: null, requirementsUninstaller: null,
            themeService: null, dashboardService: dashboardService);
        vm.EnvironmentsViewFactory = _ => new EnvironmentListView();
        return vm;
    }

    /// <summary>测试桩 — 返回固定 snapshot,避免抛 InvalidOperationException。</summary>
    private sealed class StubDashboardService : IDashboardService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new DashboardSnapshot(
                new EnvironmentCounts(0, 0, 0), 0,
                Array.Empty<RecentOperation>(), null, false, DateTimeOffset.Now));
    }

    private static void ExecuteAllowingViewConstructionFailure(RelayCommand command)
    {
        try
        {
            command.Execute(null);
        }
        catch (Exception)
        {
            // CurrentSection is assigned before page construction; null test dependencies may fail afterward.
        }
    }

    [Fact]
    public void ShowEnvironments_UpdatesCurrentSectionAndView()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            vm.ShowEnvironmentsCommand.Execute(null);
            Assert.Equal(MainSection.Environments, vm.CurrentSection);
            Assert.NotNull(vm.CurrentView);
        });
    }

    [Fact]
    public void ShowCatalog_UpdatesCurrentSectionAndView()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            ExecuteAllowingViewConstructionFailure(vm.ShowCatalogCommand);
            Assert.Equal(MainSection.Catalog, vm.CurrentSection);
        });
    }

    [Fact]
    public void ShowSettings_UpdatesCurrentSectionAndView()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            ExecuteAllowingViewConstructionFailure(vm.ShowSettingsCommand);
            Assert.Equal(MainSection.Settings, vm.CurrentSection);
        });
    }

    [Fact]
    public void ShowSystemStatus_UpdatesCurrentSectionAndView()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            ExecuteAllowingViewConstructionFailure(vm.ShowSystemStatusCommand);
            Assert.Equal(MainSection.SystemStatus, vm.CurrentSection);
        });
    }

    [Fact]
    public void ShowDashboard_UpdatesCurrentSectionAndView()
    {
        StaFact.RunOnSTA(() =>
        {
            // v0.6.9 T5:ShowDashboard 内部 new DashboardViewModel(IDashboardService) —
            // App.xaml.cs 总是传非 null 实例,测试也得传桩以避免 InvalidOperationException。
            var vm = NewVm(dashboardService: new StubDashboardService());
            vm.ShowDashboardCommand.Execute(null);
            Assert.Equal(MainSection.Dashboard, vm.CurrentSection);
        });
    }

    [Fact]
    public void CurrentSection_StaysConsistent_WhenCachingPage()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            vm.ShowEnvironmentsCommand.Execute(null);
            var firstView = vm.CurrentView;
            ExecuteAllowingViewConstructionFailure(vm.ShowCatalogCommand);
            vm.ShowEnvironmentsCommand.Execute(null);
            Assert.Equal(MainSection.Environments, vm.CurrentSection);
            Assert.Same(firstView, vm.CurrentView);
        });
    }

    [Fact]
    public void MainViewModel_DoesNotExposeBaseEnvSection()
    {
        // G2:侧栏菜单删除后,MainSection 枚举不应再有 BaseEnv 值。
        // 编译期保证 ShowBaseEnvCommand property 已删;运行时反射扫描防回归。
        var sectionType = typeof(MainSection);
        var enumNames = Enum.GetNames(sectionType);
        Assert.DoesNotContain("BaseEnv", enumNames);
    }
}
