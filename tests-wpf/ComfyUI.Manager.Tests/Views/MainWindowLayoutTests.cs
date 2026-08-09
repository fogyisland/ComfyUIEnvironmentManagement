using System;
using System.IO;
using System.Windows;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.9.3 T5:MainWindow XAML 解析 STA load test — Task 2-4 整合了 StatusBar +
/// GearIconButton + ThemeToggleButton,任何 v0.6.9.2 类型的 StaticResource 跨
/// merged-dict 解析失败会在这里浮上来。Brief 要求 DataContext=null,只测 XAML 解析,
/// 不构造真 MainViewModel(那需要 17 个 service)。
///
/// 走 StaFact.RunOnSTA — 它已处理 Application 单例 + Theme.xaml + Palette.Light.xaml
/// 合并,跨 STA thread 不会重复 new Application()("不能在同一 AppDomain 中创建多个"
/// 异常)。自己写 EnsureApplicationResources 会撞 SettingsViewLoadTests 的同款
/// race:两个 test class 并行跑时各自 _resourcesLoaded 独立、都拿 lock、然后
/// 各自 new Application 第二次抛 InvalidOperationException。
/// </summary>
public class MainWindowLayoutTests
{
    [Fact]
    public void MainWindow_Instantiation_DoesNotThrow()
    {
        // DataContext 故意 null — 只测 XAML parse / setter apply,
        // 不需要造一个真 MainViewModel(那要 17 个 service)。
        Exception? caught = null;

        StaFact.RunOnSTA(() =>
        {
            try
            {
                var w = new MainWindow();
                w.Measure(new Size(1100, 700));
                w.Arrange(new Rect(0, 0, 1100, 700));
                w.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        if (caught is not null)
        {
            throw new Exception(
                $"MainWindow load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    [Fact]
    public void MainWindow_Instantiation_WithLogFile_DoesNotThrow()
    {
        // 同时写一份 stack dump 到磁盘方便复查
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"mainwindow-load-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Exception? caught = null;

        StaFact.RunOnSTA(() =>
        {
            try
            {
                var w = new MainWindow();
                w.Measure(new Size(1100, 700));
                w.Arrange(new Rect(0, 0, 1100, 700));
                w.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        if (caught is not null)
        {
            var msg =
                $"MainWindow load FAILED at {DateTime.Now:O}\n" +
                $"Outer: {caught.GetType().FullName}: {caught.Message}\n" +
                $"Stack:\n{caught.StackTrace}\n" +
                $"--- InnerException ---\n" +
                $"{caught.InnerException?.GetType().FullName}: {caught.InnerException?.Message}\n" +
                $"Inner Stack:\n{caught.InnerException?.StackTrace}\n";
            File.WriteAllText(logPath, msg);
            throw new Exception($"MainWindow load failed — see {logPath}\n{msg}", caught);
        }

        // success 也写一行方便对比
        File.WriteAllText(logPath, $"MainWindow loaded OK at {DateTime.Now:O}");
    }
}
