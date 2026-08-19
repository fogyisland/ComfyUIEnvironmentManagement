using System;
using System.IO;
using System.Net.Http;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.20 T9:MainViewModel 接入"模型市场"侧栏分区。
/// 镜像 v0.6.19 ShowWorkflows 模式 — 加 ShowModelsCommand + ActiveSection="Models"。
/// 测试只验命令触发的状态切换,VM 构造体不真起 ModelMarketplaceService/Downloader
/// (避免网络 / I/O),只验 ShowModelsCommand → CurrentSection = MainSection.Models。
/// </summary>
public sealed class MainViewModelModelSectionTests : IDisposable
{
    private readonly string _projectRoot;

    public MainViewModelModelSectionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "main-vm-models-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewVm()
    {
        // 镜像 MainViewModelNavigationTests.NewVm 模式:仅 Settings + UiPreferencesService
        // 必须非 null(MVM ctor 校验),其它 service 全 null(VM ctor 不调它们)。
        // http 也要非 null:ShowModels 内部 lazy 构造 ModelMarketplaceViewModel +
        // CivitAiModelSource(http, ...),需要 HttpClient 实例(同 ShowWorkflows 要求)。
        return new MainViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot),
            http: new HttpClient(), workflowSymlinker: null);
    }

    [Fact]
    public void ShowModelsCommand_SetsActiveSectionToModels()
    {
        // 当前 CurrentSection 默认 Environments(枚举首项不是 Models),
        // 触发 ShowModelsCommand 后必须切到 Models(ShowWorkflows 同款断言)。
        // 注入 ModelMarketplaceViewFactory stub 返回非 WPF 对象 — 避免 UserControl
        // 在 MTA 测试线程抛 InvalidOperationException(sta init required)。
        var vm = NewVm();
        vm.ModelMarketplaceViewFactory = _ => new StubModelMarketplaceView();
        Assert.NotEqual(MainSection.Models, vm.CurrentSection);

        vm.ShowModelsCommand.Execute(null);

        Assert.Equal(MainSection.Models, vm.CurrentSection);
    }

    [Fact]
    public void ActiveSection_DefaultsToFirstSection_NotModels()
    {
        // 回归保护:加 Models 分区不能改默认 section。新建 VM 后 CurrentSection
        // 不应是 Models(避免开屏默认进模型市场页)。
        var vm = NewVm();
        Assert.NotEqual(MainSection.Models, vm.CurrentSection);
    }

    [Fact]
    public void ShowModelsCommand_IsExposedAsICommand()
    {
        // G1 接口契约:ShowModelsCommand 必须存在且可执行(供 XAML Command 绑定)。
        // null = WPF 绑 Command 会 silently no-op,加 guard 防回归。
        var vm = NewVm();
        Assert.NotNull(vm.ShowModelsCommand);
        Assert.True(vm.ShowModelsCommand.CanExecute(null));
    }

    /// <summary>
    /// 代替真实 ModelMarketplaceView(它继承 UserControl → 触发 WPF STA 初始化,
    /// 单测在 MTA 下会抛 InvalidOperationException)。同 BulkUpdateViewFactory stub
    /// pattern,只用于"VM 构造成功"路径,不验证 XAML 绑定行为。
    /// </summary>
    private sealed class StubModelMarketplaceView
    {
        public object DataContext { get; set; } = new object();
        public StubModelMarketplaceView() { }
    }
}
