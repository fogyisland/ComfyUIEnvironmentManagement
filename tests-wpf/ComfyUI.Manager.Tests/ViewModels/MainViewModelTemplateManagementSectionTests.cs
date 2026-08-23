using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views.TemplateManagement;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0 T8: MainViewModel 接入"模板管理"侧栏分区 — 9th sidebar entry (G11)。
/// 镜像 v0.6.19 ShowWorkflows / v0.6.20 ShowModels 模式 — 测试只验命令触发的
/// 状态切换 + VM/View 懒构造复用,不验 XAML 绑定行为(留给 T9)。
/// </summary>
public sealed class MainViewModelTemplateManagementSectionTests : IDisposable
{
    private readonly string _projectRoot;

    public MainViewModelTemplateManagementSectionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "main-vm-template-mgmt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewVm()
    {
        // 镜像 MainViewModelNavigationTests.NewVm 模式:仅 Settings +
        // UiPreferencesService 必须非 null(MVM ctor 校验),其它 service 全 null
        // (ShowTemplateManagement 内部不依赖任何 service,只 lazy 构造 VM + 复用
        // 已 wire up 的 _settings)。
        return new MainViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot));
    }

    [Fact]
    public void ShowTemplateManagementCommand_SetsActiveSectionToTemplates()
    {
        // G11 接口契约:点 sidebar "模板管理" RadioButton → CurrentSection = MainSection.Templates。
        var vm = NewVm();
        vm.TemplateManagementViewFactory = _ => new StubTemplateManagementView();
        Assert.NotEqual(MainSection.Templates, vm.CurrentSection);

        vm.ShowTemplateManagementCommand.Execute(null);

        Assert.Equal(MainSection.Templates, vm.CurrentSection);
    }

    [Fact]
    public void ShowTemplateManagement_IsExposedAsICommand()
    {
        // G1 接口契约:ShowTemplateManagementCommand 必须存在且可执行(XAML Command 绑定)。
        // null = WPF 绑 Command 会 silently no-op,加 guard 防回归。
        var vm = NewVm();
        Assert.NotNull(vm.ShowTemplateManagementCommand);
        Assert.True(vm.ShowTemplateManagementCommand.CanExecute(null));
    }

    [Fact]
    public void ShowTemplateManagement_LazyConstruction_CachesVmAndView()
    {
        // 跟 ShowCatalog / ShowLocalNodes 同款懒构造复用 — 同一 VM 实例跨多次 Show
        // 保留 IsBusy / SelectedRow / 编辑状态。T8 仅验 VM 不为 null + CurrentView 不空。
        var vm = NewVm();
        vm.TemplateManagementViewFactory = _ => new StubTemplateManagementView();
        vm.ShowTemplateManagementCommand.Execute(null);
        var firstView = vm.CurrentView;
        Assert.NotNull(firstView);

        vm.ShowTemplateManagementCommand.Execute(null);

        // 同一 view 实例(避免 XAML ContentControl 重复解析丢绑定的状态)。
        Assert.Same(firstView, vm.CurrentView);
    }

    [Fact]
    public void MainSection_HasTemplatesValueBetweenWorkflowsAndModels()
    {
        // G11 接口契约:MainSection 枚举按侧栏视觉顺序 — Workflows < Templates < Models。
        // 防未来 reorder 破坏 RadioButton IsChecked 转换器绑定。
        var w = (int)MainSection.Workflows;
        var t = (int)MainSection.Templates;
        var m = (int)MainSection.Models;
        Assert.True(t > w, "Templates must come after Workflows in enum");
        Assert.True(t < m, "Templates must come before Models in enum");
    }

    /// <summary>
    /// 代替真实 TemplateManagementView(UserControl → 触发 WPF STA 初始化,
    /// 单测在 MTA 下会抛 InvalidOperationException)。同 BulkUpdateViewFactory stub
    /// pattern,只用于"VM 构造成功"路径,不验证 XAML 绑定行为。
    /// </summary>
    private sealed class StubTemplateManagementView
    {
        public object DataContext { get; set; } = new object();
        public StubTemplateManagementView() { }
    }
}
