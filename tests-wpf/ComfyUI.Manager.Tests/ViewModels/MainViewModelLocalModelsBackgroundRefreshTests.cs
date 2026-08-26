using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x:用户反馈修复 — "每次点到本地模型都会刷新,这其实不对; 刷新应该是后台刷新。
/// 而且刷新也是后台执行,不要让前台冻住"。
/// ShowLocalModels 完全不 fire ReloadAsync — VM ctor 已经设 EmptyMessage placeholder
/// 提醒用户点「🔄 刷新」按钮。后续 click 走纯 cache 显示(VM 缓存,无 IO)。
/// 新数据(用户下载新模型)需显式点 toolbar「🔄 刷新」按钮触发后台 Task.Run scanner。
/// </summary>
public sealed class MainViewModelLocalModelsBackgroundRefreshTests : IDisposable
{
    private readonly string _projectRoot;

    public MainViewModelLocalModelsBackgroundRefreshTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "main-vm-local-models-bg-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewVm(Settings? settings = null)
    {
        // DefaultModelsDirectory 必须非空,否则 LocalModelsViewModel.ReloadAsync 走
        // "未配置 Models 目录" 早返路径(scanner 不被调用),counter 永远 0。
        // 测试给个 tmp 路径(目录存在与否无所谓 — scanner 在目录不存在时返空列表,
        // 仍会调用 Scan() 一次让 counter +1)。
        var s = settings ?? new Settings { DefaultModelsDirectory = _projectRoot };
        return new MainViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!,
            s, null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, new UiPreferencesService(_projectRoot));
    }

    [Fact]
    public async Task ShowLocalModels_FirstVisit_DoesNotTriggerReload()
    {
        // v1.0.0.x:用户反馈 "默认情况刷新操作不自动启动" — VM ctor 已设 EmptyMessage
        // placeholder 提示用户手动点「🔄 刷新」按钮。ShowLocalModels 第一次进 VM 构造
        // 路径(lazy),但 **不** fire ReloadAsync。scanner.ScanCount = 0。
        var vm = NewVm();
        var scanner = new CountingScanner();
        vm.LocalModelsScannerFactoryOverride = () => scanner;
        vm.LocalModelsViewFactory = _ => new StubLocalModelsView();

        vm.ShowLocalModelsCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(0, scanner.ScanCount);
    }

    [Fact]
    public async Task ShowLocalModels_SubsequentVisits_DoNotTriggerReload()
    {
        // v1.0.0.x:后续 click 走纯 cache 显示(VM 已 cache),无 IO → scanner.ScanCount 始终 0。
        var vm = NewVm();
        var scanner = new CountingScanner();
        vm.LocalModelsScannerFactoryOverride = () => scanner;
        vm.LocalModelsViewFactory = _ => new StubLocalModelsView();

        vm.ShowLocalModelsCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(0, scanner.ScanCount);

        vm.ShowLocalModelsCommand.Execute(null);   // 再次
        await Task.Delay(50);
        Assert.Equal(0, scanner.ScanCount);       // 验证:扫描次数仍为 0
    }

    [Fact]
    public async Task ShowLocalModels_ManyVisits_ZeroReloadsTotal()
    {
        // v1.0.0.x:极端测试 — 连点 5 次本地模型 tab,scanner 一次都不跑。
        // 防回归:有开发者手抖把自动 reload 加回去,这里立刻 fail。
        var vm = NewVm();
        var scanner = new CountingScanner();
        vm.LocalModelsScannerFactoryOverride = () => scanner;
        vm.LocalModelsViewFactory = _ => new StubLocalModelsView();

        for (int i = 0; i < 5; i++)
        {
            vm.ShowLocalModelsCommand.Execute(null);
            await Task.Delay(30);
        }

        Assert.Equal(0, scanner.ScanCount);
    }

    /// <summary>非阻塞 counting scanner — 每次 Scan() 增加 count 后返空列表;
    /// 测试本地模型 sidebar 首次/后续 click 触发 ReloadAsync 的次数差异。</summary>
    private sealed class CountingScanner : ModelFilesystemScanner
    {
        public int ScanCount;
        public override IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
        {
            ScanCount++;
            return Array.Empty<DownloadedModel>();
        }
    }

    /// <summary>镜像 MainViewModelLocalModelsSectionTests 的 stub View — 用纯对象代替
    /// 真实 UserControl(后者需要 STA,单测在 MTA 下抛)。</summary>
    private sealed class StubLocalModelsView
    {
        public object DataContext { get; set; } = new object();
        public StubLocalModelsView() { }
    }
}
