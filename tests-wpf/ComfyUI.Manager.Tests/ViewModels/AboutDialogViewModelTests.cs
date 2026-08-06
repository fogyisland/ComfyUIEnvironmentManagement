using System;
using System.IO;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class AboutDialogViewModelTests : IDisposable
{
    private readonly string _projectRoot;

    public AboutDialogViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "about-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Version_IsNonEmpty()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.False(string.IsNullOrEmpty(vm.Version));
    }

    [Fact]
    public void RepositoryUrl_PointsToFogyislandRepo()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.Equal("https://github.com/fogyisland/ComfyUIEnvironmentManagement", vm.RepositoryUrl);
    }

    [Fact]
    public void DonateImageFileName_IsReceiveMarkJpg()
    {
        // v0.6.5.21 hotfix:用户桌面 `asset/receiveMark.jpg` 是微信支付收款码
        Assert.Equal("receiveMark.jpg", AboutDialogViewModel.DonateImageFileName);
    }

    [Fact]
    public void DonateImageSubdirectory_IsAssetSingular()
    {
        // v0.6.5.21 hotfix:用单数 `asset/`(不是 v0.6.5.21 创建的复数 `assets/`)
        Assert.Equal("asset", AboutDialogViewModel.DonateImageSubdirectory);
    }

    [Fact]
    public void OpenDonateQrCommand_Execute_FiresOpenDonateQrRequested()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        var fired = false;
        vm.OpenDonateQrRequested += (_, _) => fired = true;
        vm.OpenDonateQrCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void CloseCommand_Execute_FiresRequestClose()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        var fired = false;
        vm.RequestClose += (_, _) => fired = true;
        vm.CloseCommand.Execute(null);
        Assert.True(fired);
    }
}
