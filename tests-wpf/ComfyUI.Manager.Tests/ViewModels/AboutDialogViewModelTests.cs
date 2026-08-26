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
        // v1.0.0:微信支付收款码文件路径 `assets/receiveMark.jpg`(复数 assets/ 统一目录)
        // 注意:v1.0.0 拆分后 DonateQrViewModel 也用同样的常量 — AboutDialogViewModel 保留
        // 此 const 作为 "全局路径契约"的源头,以便单点改名,DonateQrViewModel 自动跟随。
        Assert.Equal("receiveMark.jpg", AboutDialogViewModel.DonateImageFileName);
    }

    [Fact]
    public void DonateImageSubdirectory_IsAssetsPlural()
    {
        // v1.0.0:统一使用复数 `assets/`(目录重构后与仓库一致)
        Assert.Equal("assets", AboutDialogViewModel.DonateImageSubdirectory);
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

    // v1.0.0 拆分:OpenDonateQrCommand / OpenComfyUIGroupCommand / CoursesHeader / Course*
    // 全部从 AboutDialogViewModel 删除 — 各自搬到独立顶级 dropdown 触发的
    // DonateQrViewModel / ComfyUIGroupQrViewModel / ComfyUICoursesViewModel。
    // 删除原 OpenDonateQrCommand_Execute_FiresOpenDonateQrRequested 和
    // OpenComfyUIGroupCommand_Execute_FiresOpenComfyUIGroupRequested 2 个测试 — 它们依赖的
    // 命令/事件不再存在。
}
