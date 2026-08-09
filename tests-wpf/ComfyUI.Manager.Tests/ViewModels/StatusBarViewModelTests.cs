using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class StatusBarViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public StatusBarViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "status-bar-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_InitializesCurrentSectionNameFromMainViewModel()
    {
        var main = NewMainViewModel();
        using var statusBar = new StatusBarViewModel(main);

        Assert.Equal(MainSectionNameProvider.GetName(main.CurrentSection), statusBar.CurrentSectionName);
    }

    [Fact]
    public void CurrentSectionChanged_UpdatesCurrentSectionName()
    {
        var main = NewMainViewModel();
        using var statusBar = new StatusBarViewModel(main);

        ExecuteAllowingViewConstructionFailure(main.ShowSettingsCommand);

        Assert.Equal("设置", statusBar.CurrentSectionName);
    }

    [Fact]
    public void Version_IsAlwaysAppVersion()
    {
        using var statusBar = new StatusBarViewModel(NewMainViewModel());

        Assert.Equal(AppVersionInfo.Current, statusBar.Version);
    }

    [Fact]
    public void Dispose_UnsubscribesFromMainViewModel()
    {
        var main = NewMainViewModel();
        using var statusBar = new StatusBarViewModel(main);
        statusBar.Dispose();

        ExecuteAllowingViewConstructionFailure(main.ShowSettingsCommand);

        Assert.Equal("环境", statusBar.CurrentSectionName);
    }

    [Fact]
    public void MultipleCurrentSectionChanges_KeepNameInSync()
    {
        var main = NewMainViewModel();
        using var statusBar = new StatusBarViewModel(main);

        ExecuteAllowingViewConstructionFailure(main.ShowCatalogCommand);
        ExecuteAllowingViewConstructionFailure(main.ShowSystemStatusCommand);
        ExecuteAllowingViewConstructionFailure(main.ShowDashboardCommand);

        Assert.Equal("主页", statusBar.CurrentSectionName);
    }

    private MainViewModel NewMainViewModel() => new(
        _db.Factory,
        null!, null!, null!, null!, null!, null!, null!,
        new Settings(), null!, null!, null!, null!, null!,
        null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot));

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
}
