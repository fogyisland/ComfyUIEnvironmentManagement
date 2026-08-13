using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

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
