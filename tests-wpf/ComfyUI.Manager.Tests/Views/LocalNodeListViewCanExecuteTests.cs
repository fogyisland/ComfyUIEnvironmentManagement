using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// v0.6.15 Final Review Fix Round 1:回归测试 — C1(CommandParameter type mismatch)。
///
/// 原 bug:LocalNodeListView.xaml DataTemplate 内按钮用
/// <c>CommandParameter="{Binding}"</c>,DataTemplate DataContext 是
/// <see cref="LocalNodeListItem"/>,但 VM 的 Install/Delete 命令 canExecute predicate
/// 是 <c>info is LocalNodeInfo</c>。LocalNodeListItem 不是 LocalNodeInfo,
/// canExecute 永远返 false → 按钮在 live GUI 永久 disabled。
///
/// T3 单元测试直接传 LocalNodeInfo 给 InstallAsync/DeleteAsync(绕开 XAML→CommandParameter
/// → RelayCommand 路径),T4 STA load test 只调 InitializeComponent(不 render
/// 也不 evaluate CanExecute) — 两层都漏。修法 = XAML 改 <c>CommandParameter="{Binding Info}"</c>。
///
/// 本测试用 STA thread 渲染 LocalNodeListView,走 visual tree 找出两个按钮,
/// 调 InstallCommand.CanExecute(button.CommandParameter) — 如果 C1 没修,CommandParameter
/// 是 LocalNodeListItem,canExecute 返 false → 测试 fail,正好 catch 这个 silent disable。
/// </summary>
public class LocalNodeListViewCanExecuteTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _localDir;

    public LocalNodeListViewCanExecuteTests()
    {
        _db = new TestDb();
        _localDir = Path.Combine(Path.GetTempPath(), "local-view-caneexec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        // 创建一个真物理目录,让 LocalNodeService.ListAsync 返 ≥1 个 item
        Directory.CreateDirectory(Path.Combine(_localDir, "demo-node"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
    }

    [Fact]
    public void ItemButtons_CanExecute_IsTrue_WhenCommandParameterIsLocalNodeInfo()
    {
        Exception? caught = null;
        string? installResult = null;
        string? deleteResult = null;

        StaFact.RunOnSTA(() =>
        {
            try
            {
                var nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
                var envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
                var settings = new Settings { LocalNodeDirectory = _localDir };
                var git = new GitRunner("git");
                var nodeOps = new NodeOperations(
                    git, envRepo, nodeRepo, settings,
                    new NodeInstallDiffService((_, _, _, _) => System.Threading.Tasks.Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
                var svc = new LocalNodeService(settings, nodeRepo, envRepo, nodeOps, logger: null);
                var installer = new LocalNodeCopyInstaller(envRepo, nodeRepo, nodeOps, logger: null);

                var vm = new LocalNodeListViewModel(svc, installer, envRepo, new ErrorBannerViewModel());
                // 直接塞 1 个 item,避开 LocalNodeService.ListAsync 真 git 拉取的不确定
                var info = new LocalNodeInfo(
                    NodeId: "demo-node",
                    HeadSha: "abcdef1234567890",
                    InstallDate: DateTime.UtcNow,
                    HasPhysicalDir: true,
                    IsInDb: false,
                    InstalledEnvIds: Array.Empty<string>(),
                    InstalledEnvNames: Array.Empty<string>());
                vm.Items.Add(new LocalNodeListItem(info));

                var view = new LocalNodeListView { DataContext = vm };
                view.Measure(new Size(800, 600));
                view.Arrange(new Rect(0, 0, 800, 600));
                view.UpdateLayout();

                var buttons = FindAllButtons(view).ToList();
                if (buttons.Count < 2)
                {
                    throw new Exception(
                        $"Expected ≥ 2 buttons in visual tree (复制到 env + 删除), got {buttons.Count}. " +
                        $"Template may not be realized — Items.Count={vm.Items.Count}.");
                }

                // 按 Content 过滤出 2 个目标按钮,跟 XAML 严格对应
                var installButton = buttons.FirstOrDefault(b => b.Content as string == "复制到 env");
                var deleteButton = buttons.FirstOrDefault(b => b.Content as string == "删除");
                if (installButton is null)
                    throw new Exception("Could not find '复制到 env' button in visual tree.");
                if (deleteButton is null)
                    throw new Exception("Could not find '删除' button in visual tree.");

                // 关键断言 — C1 修复证明:CommandParameter 必须是 LocalNodeInfo,不是 LocalNodeListItem
                bool installCanExec = vm.InstallCommand.CanExecute(installButton.CommandParameter);
                bool deleteCanExec = vm.DeleteCommand.CanExecute(deleteButton.CommandParameter);

                installResult = $"param type={installButton.CommandParameter?.GetType().Name ?? "null"}, canExecute={installCanExec}";
                deleteResult = $"param type={deleteButton.CommandParameter?.GetType().Name ?? "null"}, canExecute={deleteCanExec}";

                if (!installCanExec)
                {
                    throw new Exception(
                        $"InstallCommand.CanExecute returned false on '复制到 env' button. " +
                        $"CommandParameter type={installButton.CommandParameter?.GetType().FullName ?? "null"} — " +
                        $"expected LocalNodeInfo. C1 regression: XAML binds {{Binding}} which resolves to " +
                        $"LocalNodeListItem; VM canExecute is `info is LocalNodeInfo` so always false. " +
                        $"Fix: change XAML to CommandParameter=\"{{Binding Info}}\".");
                }
                if (!deleteCanExec)
                {
                    throw new Exception(
                        $"DeleteCommand.CanExecute returned false on '删除' button. " +
                        $"CommandParameter type={deleteButton.CommandParameter?.GetType().FullName ?? "null"} — " +
                        $"expected LocalNodeInfo. Same C1 regression as install button.");
                }

                // Sanity:确认 CommandParameter 真的是 LocalNodeInfo(不是 LocalNodeListItem)
                Assert.IsType<LocalNodeInfo>(installButton.CommandParameter);
                Assert.IsType<LocalNodeInfo>(deleteButton.CommandParameter);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        if (caught is not null)
        {
            throw new Exception(
                $"CanExecute regression test failed. installResult={installResult}, deleteResult={deleteResult}. " +
                $"--- Exception ---\n{caught.GetType().FullName}: {caught.Message}\n{caught.StackTrace}",
                caught);
        }
    }

    /// <summary>
    /// Recursively walk the visual tree and return every Button descendant.
    /// </summary>
    private static IEnumerable<Button> FindAllButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button btn) yield return btn;
            foreach (var nested in FindAllButtons(child))
                yield return nested;
        }
    }
}