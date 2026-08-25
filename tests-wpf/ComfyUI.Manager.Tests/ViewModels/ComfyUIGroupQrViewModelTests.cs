using System;
using System.IO;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class ComfyUIGroupQrViewModelTests : IDisposable
{
    private readonly string _projectRoot;

    public ComfyUIGroupQrViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "comfyui-group-qr-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    [Fact]
    public void HasGroupImage_TrueWhenWechatgroupPngExists()
    {
        // v1.0.0:ComfyUI 技术组微信群二维码位于 `assets/wechatgroup.png`(复数 assets/)
        var assetDir = Path.Combine(_projectRoot, "assets");
        Directory.CreateDirectory(assetDir);
        File.WriteAllBytes(Path.Combine(assetDir, "wechatgroup.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        Assert.True(vm.HasGroupImage);
    }

    [Fact]
    public void HasGroupImage_FalseWhenPngMissing()
    {
        // assets/ 不存在或文件缺位
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        Assert.False(vm.HasGroupImage);
    }

    [Fact]
    public void CreateGroupImage_ReturnsNullWhenPngMissing()
    {
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        Assert.Null(vm.CreateGroupImage());
    }

    [Fact]
    public void GroupImagePath_CombinesAssetSubdirectoryAndFilename()
    {
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        var expected = Path.Combine(_projectRoot, "assets", "wechatgroup.png");
        Assert.Equal(expected, vm.GroupImagePath);
    }

    [Fact]
    public void CloseCommand_Execute_FiresRequestClose()
    {
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        var fired = false;
        vm.RequestClose += (_, _) => fired = true;
        vm.CloseCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void CanExecute_IsTrueForAllCommands()
    {
        var vm = new ComfyUIGroupQrViewModel(_projectRoot);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    [Fact]
    public void GroupImageFileName_IsWechatgroupPng()
    {
        // v1.0.0:微信群二维码文件名固定 `wechatgroup.png`
        Assert.Equal("wechatgroup.png", ComfyUIGroupQrViewModel.GroupImageFileName);
    }
}
