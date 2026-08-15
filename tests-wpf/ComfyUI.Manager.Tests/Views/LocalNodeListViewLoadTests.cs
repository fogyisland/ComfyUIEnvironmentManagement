using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.15 T4:LocalNodeListView XAML STA-thread headless load test。follow
/// <see cref="EnvPickerDialogLoadTests"/> 模式用 <see cref="StaFact.RunOnSTA"/>
/// 把 XAML 解析 + InitializeComponent 调到 STA thread(避免 WPF STA 线程模型错误)。
/// 任一资源 key 缺失 / Setter DynamicResource 写法错(theme Setter StaticResource
/// 必须 property-element form;v0.6.9.2 lesson)/ DataTemplate binding 错都会在
/// ctor 阶段抛 XamlParseException。
///
/// VM 用 T3 既有的 TestDb + NodeRepository + EnvironmentRepository + Settings
/// + GitRunner + NodeOperations 全套真实 test dep,这样 DataContextChanged → 自动
/// refresh 不会 NRE 在空 LocalNodeService deps 上。
/// </summary>
public class LocalNodeListViewLoadTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _localDir;

    public LocalNodeListViewLoadTests()
    {
        _db = new TestDb();
        _localDir = Path.Combine(Path.GetTempPath(), "local-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
    }

    [Fact]
    public void Constructor_LoadsXaml_NoException()
    {
        var nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
        var envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        var settings = new Settings { LocalNodeDirectory = _localDir };
        var git = new GitRunner("git");
        var nodeOps = new NodeOperations(
            git, envRepo, nodeRepo, settings,
            new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
        var svc = new LocalNodeService(settings, nodeRepo, envRepo, nodeOps, logger: null);
        var installer = new LocalNodeCopyInstaller(envRepo, nodeRepo, nodeOps, logger: null);
        var reqInstaller = new RequirementsInstaller();

        StaFact.RunOnSTA(() =>
        {
            var vm = new LocalNodeListViewModel(svc, installer, envRepo, nodeRepo, reqInstaller, new ErrorBannerViewModel());
            var view = new LocalNodeListView { DataContext = vm };
            Assert.NotNull(view);
        });
    }
}