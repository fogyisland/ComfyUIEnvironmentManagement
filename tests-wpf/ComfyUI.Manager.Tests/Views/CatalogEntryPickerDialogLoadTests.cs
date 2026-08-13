using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.14 T2 R1:STA-thread headless load test,follow BaseEnvProfilePickerDialogTests
/// 的 pattern。brief 没有强制要求,但 v0.6.9.2 postmortem 明确"新增/重写 view 必加 STA
/// load test"(feedback_wpf_style_setter_dynamic_resource.md 第 4 条)。本 dialog 加了
/// 1 个新 converter key(EnumEqualsConverter)+ 1 个新行内"安装"按钮 +
/// 4 个 style 引用 — XAML 解析期才会暴露错误,编译期发现不了。
/// </summary>
public class CatalogEntryPickerDialogLoadTests
{
    private static CatalogEntryPickerViewModel NewVm()
    {
        // 构造 VM 不需要 db 数据,BuildItems() 内部空 catalog / 空 scanned_nodes 即可。
        var catalogRepo = new CatalogRepository(new CatalogCacheStore());
        var nodeRepo = new NodeRepository(new SqliteConnectionFactory());
        var envRepo = new EnvironmentRepository(new SqliteConnectionFactory());
        var ops = new TestOnlyOps(envRepo, nodeRepo);
        return new CatalogEntryPickerViewModel(
            catalogRepo, nodeRepo, ops, envId: "env-test", logger: null);
    }

    [Fact]
    public void Ctor_ParsesXamlAndResolvesStaticResources()
    {
        StaFact.RunOnSTA(() =>
        {
            // InitializeComponent 会解析 BackgroundBrush / MaterialButton / DangerButton /
            // SurfaceBrush / PrimaryBrush / CatalogSegmentedRadioButton / CatalogCardItemContainerStyle /
            // CatalogInstallTypeBadgeStyle / EnumEqualsConverter 等;
            // 任一缺失即抛 XamlParseException。
            var vm = NewVm();
            var dlg = new CatalogEntryPickerDialog(vm);
            Assert.NotNull(dlg);
        });
    }

    /// <summary>
    /// R1 fix 行为验证:设 ActiveFilter = NotInstalled,ListBox Items 应该重新筛选
    /// (无 catalog entries → Items.Count 仍 0,但 rebuild pipeline 不抛)。这一条
    /// 验证 filter chip 点击 → ApplyFilter() → Items.Clear() + Add 全链路通。
    /// </summary>
    [Fact]
    public void Ctor_SettingActiveFilterToNotInstalled_DoesNotThrow()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = NewVm();
            vm.ActiveFilter = PickerFilter.NotInstalled;
            vm.ActiveFilter = PickerFilter.Installed;
            vm.ActiveFilter = PickerFilter.Outdated;
            vm.ActiveFilter = PickerFilter.All;
            Assert.Empty(vm.Items);  // 空 catalog → 0 items 一直成立
        });
    }

    /// <summary>
    /// v0.6.14 T2 R2 Critical 1 end-to-end test:RadioButton.Tag 必须存 PickerFilterOption
    /// wrapper(不能存 .Filter enum),否则 OnFilterChipClicked 里的
    /// <c>rb.Tag is PickerFilterOption opt</c> 永远 cast 失败 → ActiveFilter 永远不变 →
    /// chip 点击无效。R1 写 XAML 用了 <c>Tag="{Binding Filter}"</c>(提取 enum),R2 改
    /// <c>Tag="{Binding}"</c>(存 wrapper)修这个 bug;此测试钉 cast pattern
    /// + click → ActiveFilter → ApplyFilter 全链路,prevent 回归。
    ///
    /// <para>为什么不直接枚举 dialog visual tree 的 RadioButton:
    /// <c>ItemsControl</c> 在 headless <c>Measure/Arrange</c> 路径下容器生成不可靠
    /// (ElementRealization 待到 <c>ItemsPresenter</c> 第一次真实 layout pass 才触发);
    /// 这里直接构造 RadioButton 实例 + 设 Tag + 触发 Click 事件模拟用户操作,
    /// 测的是 handler 行为 — 跟 XAML 写入 wrapper 进 Tag 是同一结果。</para>
    /// </summary>
    [Fact]
    public void ClickFilterChip_UpdatesActiveFilter_AndRefiltersItems()
    {
        using var db = new TestDb();
        SeedCatalogEntry(db, "pkg-installed", latestVersion: "1.0.0");
        SeedCatalogEntry(db, "pkg-fresh");
        SeedEnv(db, "env-1");
        SeedScannedNode(db, "env-1", "pkg-installed", installedTag: "1.0.0");

        var vm = NewVmWithDb(db);
        Assert.Equal(2, vm.Items.Count);  // sanity:1 installed + 1 not-installed

        StaFact.RunOnSTA(() =>
        {
            // 构造 dialog,把 VM 绑上(让 OnFilterChipClicked 通过 DataContext 拿到 vm)
            var dlg = new CatalogEntryPickerDialog(vm);

            // 模拟 XAML 写 Tag 的真值:每个 chip 的 Tag 是 wrapper,不是 enum。
            // R1 Critical 1 bug:XAML 写 Tag="{Binding Filter}" → 存 enum → cast 失败。
            // R2 修:写 Tag="{Binding}" → 存 wrapper → cast 成功 → handler 写 vm.ActiveFilter。
            // 我们的测试从 VM.FilterOptions 里拿真 wrapper 塞进 Tag(等价于 R2 XAML 行为)。
            var notInstalledOpt = vm.FilterOptions.Single(
                o => o.Filter == PickerFilter.NotInstalled);

            // 模拟一个 chip RadioButton,Tag 设成 wrapper(跟 R2 XAML 行为一致)
            var chip = new System.Windows.Controls.RadioButton
            {
                GroupName = "PickerFilter",
                Tag = notInstalledOpt,
                Content = notInstalledOpt.Label,
            };
            // Click handler 必须能拿 wrapper,设 wire-up(R2 XAML 已 wire,这里 reflective 调)
            // 直接 invoke 私有方法:模拟 WPF routing Click event → OnFilterChipClicked
            var handler = typeof(CatalogEntryPickerDialog).GetMethod(
                "OnFilterChipClicked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            handler.Invoke(dlg, new object[]
            {
                chip,
                new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent),
            });

            // handler 写 vm.ActiveFilter = NotInstalled → ApplyFilter rebuild Items
            Assert.Equal(PickerFilter.NotInstalled, vm.ActiveFilter);
            // ApplyFilter 只留 NotInstalled 条 = pkg-fresh
            Assert.Single(vm.Items);
            Assert.Equal("pkg-fresh", vm.Items[0].Entry.Package);

            // 反向再确认:Tag 设成裸 enum(handler 应该不抛但 ActiveFilter 不变)— 这是
            // R1 bug 的"还原"演示:跟 R2 fix 形成对照,证明 wrapper 是 cast 必需。
            vm.ActiveFilter = PickerFilter.All;  // 重置
            var brokenChip = new System.Windows.Controls.RadioButton
            {
                GroupName = "PickerFilter",
                Tag = PickerFilter.NotInstalled,  // R1 bug:enum 直接
            };
            handler.Invoke(dlg, new object[]
            {
                brokenChip,
                new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent),
            });
            // R1 bug:cast 失败 → handler 啥也没做 → ActiveFilter 保留 All
            Assert.Equal(PickerFilter.All, vm.ActiveFilter);
            Assert.Equal(2, vm.Items.Count);  // 没 refilter
        });
    }

    private static void SeedEnv(TestDb db, string id)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        envRepo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"/tmp/{id}",
            ComfyuiLayout = "isolated",
            CustomNodesPath = $"/tmp/{id}/custom_nodes",
            Port = 8188,
            Status = "stopped",
        });
    }

    private static void SeedCatalogEntry(TestDb db, string package,
        string? latestVersion = null)
    {
        var entry = new CatalogEntry
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = "https://example.com/catalog.json",
            Package = package,
            CachedAt = "2026-08-01T00:00:00",
            ExpiresAt = "2027-08-01T00:00:00",
            RawMetadata = new Dictionary<string, object?>(),
            InstallType = "git",
        };
        var repo = new CatalogRepository(new CatalogCacheStore(db.Path));
        repo.Upsert(entry);
        if (latestVersion is not null)
        {
            repo.UpdateLatestVersions(new[] { (entry.SourceUrl, entry.Package, latestVersion) });
        }
    }

    private static void SeedScannedNode(TestDb db, string envId, string package,
        string? installedTag = null)
    {
        var scanMeta = new Dictionary<string, string>();
        if (installedTag is not null) scanMeta["installed_tag"] = installedTag;
        new NodeRepository(db.Factory).Upsert(new ScannedNode
        {
            Id = package,
            EnvId = envId,
            Package = package,
            PackagePath = $"/tmp/{envId}/custom_nodes/{package}",
            ScanMeta = scanMeta,
        });
    }

    private static CatalogEntryPickerViewModel NewVmWithDb(TestDb db)
    {
        var envRepo = new EnvironmentRepository(db.Factory);
        var nodeRepo = new NodeRepository(db.Factory);
        var ops = new TestOnlyOps(envRepo, nodeRepo);
        return new CatalogEntryPickerViewModel(
            new CatalogRepository(new CatalogCacheStore(db.Path)),
            nodeRepo,
            ops,
            envId: "env-1",
            logger: null);
    }

    /// <summary>
    /// TestOnlyOps:不调真 git,什么都不返。仅 ctor 用,不 override UninstallAsync
    /// 也没关系 — STA load test 不触发卸载路径。
    /// </summary>
    private sealed class TestOnlyOps : NodeOperations
    {
        public TestOnlyOps(EnvironmentRepository envRepo, NodeRepository nodeRepo)
            : base(new GitRunner("git"), envRepo, nodeRepo, new Settings(),
                   new NodeInstallDiffService((_, _, _, _) =>
                       System.Threading.Tasks.Task.FromResult(
                           new ProcessResult(true, 0, "[]", ""))))
        { }
    }
}

