using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainViewModelMenuTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelMenuTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "main-vm-menu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private sealed class Capture
    {
        public string? Folder;
        public bool ExitInvoked;
        public bool DonateQrInvoked;
    }

    private MainViewModel NewMainVm(Capture cap)
    {
        var svc = new UiPreferencesService(_projectRoot);
        var main = new MainViewModel(
            _db.Factory, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, svc);
        main.EnvironmentsViewFactory = vm => new object();  // 避 STA,跟 v0.6.5.20 同款
        main.OpenFolderOverride = p => cap.Folder = p;
        main.ExitAppOverride = () => cap.ExitInvoked = true;
        main.ShowDonateQrOverride = () => cap.DonateQrInvoked = true;  // v0.6.5.21 hotfix
        return main;
    }

    [Fact]
    public void AllSevenMenuCommands_CanExecuteIsTrue()
    {
        // v0.6.5.21 hotfix:加 ShowDonateQrCommand(共 7 个 menu command)
        var cap = new Capture();
        var main = NewMainVm(cap);
        Assert.True(main.SaveUiPreferencesCommand.CanExecute(null));
        Assert.True(main.LoadUiPreferencesCommand.CanExecute(null));
        Assert.True(main.OpenProjectFolderCommand.CanExecute(null));
        Assert.True(main.OpenLogFolderCommand.CanExecute(null));
        Assert.True(main.ExitAppCommand.CanExecute(null));
        Assert.True(main.ShowAboutCommand.CanExecute(null));
        Assert.True(main.ShowDonateQrCommand.CanExecute(null));
    }

    [Fact]
    public void OpenProjectFolderCommand_DelegatesToOpenFolderOverride()
    {
        var cap = new Capture();
        var main = NewMainVm(cap);
        main.OpenProjectFolderCommand.Execute(null);
        Assert.NotNull(cap.Folder);
        Assert.Equal(_projectRoot, cap.Folder!);
    }

    [Fact]
    public void OpenLogFolderCommand_DelegatesToOpenFolderOverride()
    {
        var cap = new Capture();
        var main = NewMainVm(cap);
        main.OpenLogFolderCommand.Execute(null);
        Assert.NotNull(cap.Folder);
        Assert.Equal(Path.Combine(_projectRoot, "Logs"), cap.Folder!);
    }

    [Fact]
    public void ExitAppCommand_DelegatesToExitAppOverride()
    {
        var cap = new Capture();
        var main = NewMainVm(cap);
        main.ExitAppCommand.Execute(null);
        Assert.True(cap.ExitInvoked);
    }

    [Fact]
    public void SaveUiPreferencesCommand_PopSaveDialogOverride_DelegatesToOverride()
    {
        var cap = new Capture();
        var main = NewMainVm(cap);
        var capturedPath = (string?)null;
        var capturedPrefs = (UiPreferences?)null;
        main.SaveUiPreferencesDialogOverride = (path, prefs) =>
        {
            capturedPath = path;
            capturedPrefs = prefs;
            return true;
        };
        // 关窗时调 SaveToFile——这里模拟关窗:直接 Execute + 注入一个能调到的 prefs 来源
        // 因为命令体需要当前 prefs,需要一个简单回调:在命令体里通过 _uiPreferencesService 读一次
        main.SaveUiPreferencesCommand.Execute(null);
        Assert.NotNull(capturedPath);
    }

    [Fact]
    public void LoadUiPreferencesCommand_PopOpenDialogOverride_DelegatesToOverride()
    {
        var cap = new Capture();
        var main = NewMainVm(cap);
        var capturedPath = (string?)null;
        main.LoadUiPreferencesDialogOverride = path => { capturedPath = path; return true; };
        main.LoadUiPreferencesCommand.Execute(null);
        Assert.NotNull(capturedPath);
    }

    [Fact]
    public void ShowDonateQrCommand_DelegatesToShowDonateQrOverride()
    {
        // v0.6.5.21 hotfix:菜单"赞助作者..."绑 ShowDonateQrCommand,测试 seam 替代真弹窗
        var cap = new Capture();
        var main = NewMainVm(cap);
        main.ShowDonateQrCommand.Execute(null);
        Assert.True(cap.DonateQrInvoked);
    }
}
