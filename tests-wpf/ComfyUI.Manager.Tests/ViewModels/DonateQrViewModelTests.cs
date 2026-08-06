using System;
using System.IO;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class DonateQrViewModelTests : IDisposable
{
    private readonly string _projectRoot;

    public DonateQrViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "donate-qr-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void HasDonateImage_TrueWhenReceiveMarkJpgExists()
    {
        // v0.6.5.21 hotfix:用户桌面 `asset/receiveMark.jpg`(单数)就是微信支付收款码
        var assetDir = Path.Combine(_projectRoot, "asset");
        Directory.CreateDirectory(assetDir);
        File.WriteAllBytes(Path.Combine(assetDir, "receiveMark.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        var vm = new DonateQrViewModel(_projectRoot);
        Assert.True(vm.HasDonateImage);
    }

    [Fact]
    public void HasDonateImage_FalseWhenPngMissing()
    {
        // asset/ 不存在或文件缺位
        var vm = new DonateQrViewModel(_projectRoot);
        Assert.False(vm.HasDonateImage);
    }

    [Fact]
    public void CreateDonateImage_ReturnsNullWhenPngMissing()
    {
        var vm = new DonateQrViewModel(_projectRoot);
        Assert.Null(vm.CreateDonateImage());
    }

    [Fact]
    public void DonateImagePath_CombinesAssetSubdirectoryAndFilename()
    {
        var vm = new DonateQrViewModel(_projectRoot);
        var expected = Path.Combine(_projectRoot, "asset", "receiveMark.jpg");
        Assert.Equal(expected, vm.DonateImagePath);
    }

    [Fact]
    public void CloseCommand_Execute_FiresRequestClose()
    {
        var vm = new DonateQrViewModel(_projectRoot);
        var fired = false;
        vm.RequestClose += (_, _) => fired = true;
        vm.CloseCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void CanExecute_IsTrueForAllCommands()
    {
        var vm = new DonateQrViewModel(_projectRoot);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }
}
