using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0 T3: MainViewModel 接入"本地模型"侧栏分区 — 镜像 v1.0.0 T8 模板管理测试
/// 模式 — 测试只验命令触发的状态切换 + VM/View 懒构造复用,不验 XAML 绑定行为(留给 T4)。
/// </summary>
public sealed class MainViewModelLocalModelsSectionTests : IDisposable
{
    private readonly string _projectRoot;

    public MainViewModelLocalModelsSectionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "main-vm-local-models-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewVm()
    {
        // 镜像 MainViewModelTemplateManagementSectionTests.NewVm:仅 Settings +
        // UiPreferencesService 必须非 null(MVM ctor 校验),其它 service 全 null
        // (ShowLocalModels 内部不依赖任何 service,只 lazy 构造 VM + 复用 _settings)。
        return new MainViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot));
    }

    [Fact]
    public void ShowLocalModelsCommand_SetsActiveSectionToLocalModels()
    {
        // G13 接口契约:点 sidebar "本地模型" RadioButton → CurrentSection = MainSection.LocalModels。
        var vm = NewVm();
        vm.LocalModelsViewFactory = _ => new StubLocalModelsView();
        Assert.NotEqual(MainSection.LocalModels, vm.CurrentSection);

        vm.ShowLocalModelsCommand.Execute(null);

        Assert.Equal(MainSection.LocalModels, vm.CurrentSection);
    }

    [Fact]
    public void ShowLocalModels_IsExposedAsICommand()
    {
        // G1 接口契约:ShowLocalModelsCommand 必须存在且可执行(XAML Command 绑定)。
        // null = WPF 绑 Command 会 silently no-op,加 guard 防回归。
        var vm = NewVm();
        Assert.NotNull(vm.ShowLocalModelsCommand);
        Assert.True(vm.ShowLocalModelsCommand.CanExecute(null));
    }

    [Fact]
    public void ShowLocalModels_LazyConstruction_CachesVmAndView()
    {
        // 跟 ShowCatalog / ShowTemplateManagement 同款懒构造复用 — 同一 VM 实例跨多次 Show
        // 保留 kind chip 选中 / sort / 滚动位置。T3 仅验 VM 不为 null + CurrentView 不空 +
        // 第二次 Show 复用同一 view(避免 XAML ContentControl 重复解析丢绑定状态)。
        var vm = NewVm();
        vm.LocalModelsViewFactory = _ => new StubLocalModelsView();
        vm.ShowLocalModelsCommand.Execute(null);
        var firstView = vm.CurrentView;
        Assert.NotNull(firstView);

        vm.ShowLocalModelsCommand.Execute(null);

        Assert.Same(firstView, vm.CurrentView);
    }

    [Fact]
    public void MainSection_HasLocalModelsBetweenWorkflowsAndTemplates()
    {
        // G13 接口契约:MainSection 枚举按侧栏视觉顺序(spec §4.2)— Workflows < LocalModels < Templates。
        // 防未来 reorder 破坏 RadioButton IsChecked 转换器绑定 + sidebar.inf 默认值。
        var w = (int)MainSection.Workflows;
        var l = (int)MainSection.LocalModels;
        var t = (int)MainSection.Templates;
        Assert.True(l > w, "LocalModels must come after Workflows in enum");
        Assert.True(l < t, "LocalModels must come before Templates in enum");
    }

    /// <summary>
    /// 代替真实 LocalModelsView(UserControl → 触发 WPF STA 初始化,
    /// 单测在 MTA 下会抛 InvalidOperationException)。同 TemplateManagementViewFactory stub
    /// pattern,只用于"VM 构造成功"路径,不验证 XAML 绑定行为。
    /// </summary>
    private sealed class StubLocalModelsView
    {
        public object DataContext { get; set; } = new object();
        public StubLocalModelsView() { }
    }
}