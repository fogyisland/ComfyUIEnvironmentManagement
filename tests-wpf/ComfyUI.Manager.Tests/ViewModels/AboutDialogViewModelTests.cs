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
    public void HasDonateImage_TrueWhenPngExists()
    {
        var assetsDir = Path.Combine(_projectRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllBytes(Path.Combine(assetsDir, "wechat-donate.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.True(vm.HasDonateImage);
    }

    [Fact]
    public void HasDonateImage_FalseWhenPngMissing()
    {
        // assets/ 不存在或文件缺位
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.False(vm.HasDonateImage);
    }

    [Fact]
    public void CreateDonateImage_ReturnsNullWhenPngMissing()
    {
        var vm = new AboutDialogViewModel(_projectRoot);
        Assert.Null(vm.CreateDonateImage());
    }
}
