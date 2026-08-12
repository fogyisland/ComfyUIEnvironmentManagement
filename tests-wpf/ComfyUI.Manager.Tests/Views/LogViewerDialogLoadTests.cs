using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.12 STA 集成测试:实测 LogViewer (UserControl) + LogTailer + LogViewerViewModel
/// 在 STA UI 线程下能否把文件内容渲染到 ListBox。
///
/// 用户报"env-list 点查看日志 -> 日志为空的"。本测试创建 1 个真 log 文件 + 真 LogTailer +
/// 真 LogViewerViewModel,把 LogViewer UserControl Measure/Arrange/UpdateLayout,
/// 断言 ListBox.Items.Count > 0。如果 FAIL -> 数据绑定 bug。
///
/// 跟 brief 不一样:brief 用 _dlg.Show(),但 Window.Show() 在 STA helper thread
/// 里会让 test host 进程崩溃(同 ThemeToggleButtonTests 的 MainWindow 测试绕开
/// Show() 只做 Measure/Arrange 的同款 pattern)。LogViewerDialog.xaml 只是
/// 套了 LogViewer UserControl + 设 DataContext,直接测 LogViewer 等效覆盖 dialog。
///
/// Test plan:
/// - PASS-shape: 真文件 3 行 -> 断言 ListBox.Items.Count > 0
/// - FAIL-shape: 空文件(0 字节)-> 断言 ListBox.Items.Count == 0 (sanity baseline)
/// </summary>
public class LogViewerDialogLoadTests : IDisposable
{
    private readonly string _tmpFile;

    public LogViewerDialogLoadTests()
    {
        WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Light);
        _tmpFile = Path.Combine(Path.GetTempPath(), $"logviewer-test-{Guid.NewGuid():N}.log");
        File.WriteAllLines(_tmpFile, new[]
        {
            "[12:00:00] line 1",
            "[12:00:01] line 2",
            "[12:00:02] line 3",
        });
    }

    public void Dispose()
    {
        try { File.Delete(_tmpFile); } catch { }
    }

    [Fact]
    public void LogViewer_LoadsFileLines_IntoListBox()
    {
        Exception? caught = null;
        int itemsCount = -1;

        StaFact.RunOnSTA(() =>
        {
            LogTailer? tailer = null;
            try
            {
                tailer = new LogTailer(_tmpFile, TimeSpan.FromMilliseconds(50));
                var vm = new LogViewerViewModel("env-test", tailer);
                var view = new LogViewer { DataContext = vm };
                view.Measure(new Size(800, 600));
                view.Arrange(new Rect(0, 0, 800, 600));
                view.UpdateLayout();

                var listBox = FindListBox(view);
                if (listBox is null)
                {
                    throw new Exception("FindListBox returned null — no ListBox found in LogViewer visual tree");
                }
                itemsCount = listBox.Items.Count;
                if (listBox.Items.Count == 0)
                {
                    throw new Exception(
                        $"ListBox should show lines, got {listBox.Items.Count}. " +
                        $"VM.Lines.Count={vm.Lines.Count}. " +
                        $"File length={new FileInfo(_tmpFile).Length} bytes.");
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                try { tailer?.Stop(); } catch { }
            }
        });

        if (caught is not null)
        {
            throw new Exception(
                $"LogViewer load failed (itemsCount={itemsCount}): {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    [Fact]
    public void LogViewer_EmptyFile_RendersEmptyListBox()
    {
        var emptyFile = Path.Combine(Path.GetTempPath(), $"logviewer-empty-{Guid.NewGuid():N}.log");
        File.Create(emptyFile).Dispose();
        try
        {
            Exception? caught = null;
            int itemsCount = -1;

            StaFact.RunOnSTA(() =>
            {
                LogTailer? tailer = null;
                try
                {
                    tailer = new LogTailer(emptyFile, TimeSpan.FromMilliseconds(50));
                    var vm = new LogViewerViewModel("env-empty", tailer);
                    var view = new LogViewer { DataContext = vm };
                    view.Measure(new Size(800, 600));
                    view.Arrange(new Rect(0, 0, 800, 600));
                    view.UpdateLayout();

                    var listBox = FindListBox(view);
                    if (listBox is null)
                    {
                        throw new Exception("FindListBox returned null — no ListBox found in LogViewer visual tree");
                    }
                    itemsCount = listBox.Items.Count;
                    if (listBox.Items.Count != 0)
                    {
                        throw new Exception(
                            $"ListBox should be empty for empty file, got {listBox.Items.Count}. " +
                            $"VM.Lines.Count={vm.Lines.Count}.");
                    }
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    try { tailer?.Stop(); } catch { }
                }
            });

            if (caught is not null)
            {
                throw new Exception(
                    $"LogViewer empty-file load failed (itemsCount={itemsCount}): {caught.GetType().FullName}: {caught.Message}\n" +
                    $"--- InnerException ---\n{caught.InnerException}\n" +
                    $"--- StackTrace ---\n{caught.StackTrace}",
                    caught);
            }
        }
        finally
        {
            try { File.Delete(emptyFile); } catch { }
        }
    }

    private static ListBox? FindListBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBox lb) return lb;
            var nested = FindListBox(child);
            if (nested != null) return nested;
        }
        return null;
    }
}