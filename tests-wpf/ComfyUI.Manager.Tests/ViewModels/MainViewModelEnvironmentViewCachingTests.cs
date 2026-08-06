using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.5.20 hotfix:离开"环境"再回来,ShowEnvironmentsCommand 必须复用同一
/// EnvironmentListViewModel。否则进行中的装依赖状态(RequirementsStatus)
/// 随页面销毁丢失,用户回到"环境"时看到空面板,再次点击"装依赖"就会
/// 并发触发第二次 pip(日志里就出现连续 2 次 "开始装 requirements.txt",
/// 第二次其实没 marker 但 pip 已装好的包覆盖)。
///
/// ShowEnvironmentsCommand 是 side-effect-free 的(不真起后台),只验
/// "两次调"返回同一 VM + 同一 View 引用即可,不依赖 Launcher / EnvCreator 等
/// 真实组件。
/// </summary>
public sealed class MainViewModelEnvironmentViewCachingTests
{
    private static MainViewModel NewMainVm(TestDb db)
    {
        // 任何 ShowEnvironments 不会实际触发的 service 都传 null(VM ctor 不调用它们);
        // 唯一真用的是 SqliteConnectionFactory + EnvironmentRepository(构造时读 env 表)。
        return new MainViewModel(
            db.Factory,
            null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!,
            null!, "", "", null!, null!);
    }

    [Fact]
    public void ShowEnvironmentsCommand_CalledTwice_ReturnsSameViewModelInstance()
    {
        using var db = new TestDb();
        var main = NewMainVm(db);
        // 测试环境下用 stub 取代真 EnvironmentListView(会触发 WPF STA 初始化)
        main.EnvironmentsViewFactory = vm => new StubView(vm);

        Assert.Null(main.CurrentEnvironmentsViewModel);

        main.ShowEnvironmentsCommand.Execute(null);
        var first = main.CurrentEnvironmentsViewModel;
        Assert.NotNull(first);

        // 直接再次触发 ShowEnvironmentsCommand — 模拟用户"离开 → 回来"
        // 路径(中间真正切到别的 tab 会触发其他 View 的 STA 初始化,绕过;
        // 缓存行为只关心"再次进来"这一步,跟中间怎么走无关)。
        main.ShowEnvironmentsCommand.Execute(null);

        Assert.Same(first, main.CurrentEnvironmentsViewModel);
    }

    [Fact]
    public void ShowEnvironmentsCommand_CurrentViewIsTheCachedEnvironmentView()
    {
        // 关键:CurrentView 引用也必须是缓存的 EnvironmentListView,
        // 不然 XAML ContentControl 重新解析又会 new 一份绑定,RequirementsStatus
        // 仍然丢。
        using var db = new TestDb();
        var main = NewMainVm(db);
        main.EnvironmentsViewFactory = vm => new StubView(vm);

        main.ShowEnvironmentsCommand.Execute(null);
        var firstView = main.CurrentEnvironmentsViewModel;  // sanity: VM 已 cache
        Assert.NotNull(firstView);
        var firstViewRef = main.CurrentView;
        main.ShowEnvironmentsCommand.Execute(null);

        Assert.Same(firstViewRef, main.CurrentView);
    }

    /// <summary>
    /// 代替真实 EnvironmentListView(它继承 UserControl → 触发 WPF STA 初始化,
    /// 单测在 MTA 下会抛 InvalidOperationException)。只用于断言"VM 缓存 + 同一
    /// CurrentView 引用",不验证 XAML 绑定行为。
    /// </summary>
    private sealed class StubView
    {
        public object DataContext { get; set; } = new object();
        public StubView(object dataContext) { DataContext = dataContext; }
    }
}
