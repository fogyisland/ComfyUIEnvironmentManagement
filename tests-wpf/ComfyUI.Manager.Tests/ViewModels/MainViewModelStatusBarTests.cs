using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.9.3 T5:MainViewModel.StatusBar property 暴露 + 初始化正确性。
/// StatusBarViewModel 自身测试在 StatusBarViewModelTests.cs。这里只覆盖
/// MainViewModel 那一侧(property exists / 初始化值跟 MainViewModel.CurrentSection 同步 /
/// Version 走 AppVersionInfo.Current)。
/// </summary>
public sealed class MainViewModelStatusBarTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelStatusBarTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "main-vm-statusbar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewMainViewModel() => new(
        _db.Factory,
        null!, null!, null!, null!, null!, null!, null!,
        new Settings(), null!, null!, null!, null!, null!,
        null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot));

    [Fact]
    public void StatusBar_PropertyIsExposed()
    {
        // v0.6.9.3 T2:MainWindow XAML 绑 MainViewModel.StatusBar → property 必须存在且非 null
        var main = NewMainViewModel();

        Assert.NotNull(main.StatusBar);
    }

    [Fact]
    public void StatusBar_CurrentSectionName_InitialMatchesMainViewModelCurrentSection()
    {
        // 默认 CurrentSection=Environments → StatusBar.CurrentSectionName = "环境"
        var main = NewMainViewModel();

        Assert.Equal(MainSectionNameProvider.GetName(main.CurrentSection), main.StatusBar.CurrentSectionName);
    }

    [Fact]
    public void StatusBar_Version_EqualsAppVersionInfoCurrent()
    {
        // v0.6.9.3 T2:StatusBar.Version 绑 AppVersionInfo.Current,不依赖 assembly attribute
        var main = NewMainViewModel();

        Assert.Equal(AppVersionInfo.Current, main.StatusBar.Version);
    }
}
