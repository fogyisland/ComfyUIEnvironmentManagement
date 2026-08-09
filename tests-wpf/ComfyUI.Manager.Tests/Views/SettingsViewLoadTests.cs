using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Resources;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// 诊断用:不弹窗 headless 加载 SettingsView,捕获 XAML 解析异常。
/// 用户桌面点击"设置"时 app 直接退出的根因 = Theme.xaml:141 MaterialTextBox
/// 的 StaticResource SecondaryBrush 在 Setter 里解析失败(同 v0.6.9.2 MaterialButton
/// 那条 hotfix),SettingsView XAML 解析到 20+ 个 TextBox 时抛 XamlParseException。
/// </summary>
public class SettingsViewLoadTests
{
    public SettingsViewLoadTests()
    {
        // 模拟 App.xaml 的 ResourceDictionary 合并 — 没这步 MaterialTextBox 不在
        // Application.Current.Resources,测试因"找不到资源"抛错但不是真根因。
        // 用 pack URI 走 ComponentResourceKey 同样的程序集资源解析,等价于生产
        // 启动时 App.xaml.cs 把 Theme.xaml + Palette.Dark.xaml 合并进
        // Application.Current.Resources.MergedDictionaries 的行为。
        EnsureApplicationResources();
    }

    private static bool _resourcesLoaded;
    private static readonly object _lock = new();

    private static void EnsureApplicationResources()
    {
        if (_resourcesLoaded) return;
        lock (_lock)
        {
            if (_resourcesLoaded) return;
            // WPF Application 必须存在才能合并 ResourceDictionary
            if (Application.Current is null)
            {
                // 测试线程不持有 Application 句柄,造一个
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            // 加载 Theme.xaml
            var themeUri = new Uri(
                "/ComfyUI.Manager;component/Resources/Theme.xaml",
                UriKind.Relative);
            var theme = new ResourceDictionary { Source = themeUri };
            // 加载 Palette.Dark.xaml
            var paletteUri = new Uri(
                "/ComfyUI.Manager;component/Themes/Palette.Dark.xaml",
                UriKind.Relative);
            var palette = new ResourceDictionary { Source = paletteUri };
            Application.Current.Resources.MergedDictionaries.Add(theme);
            Application.Current.Resources.MergedDictionaries.Add(palette);
            _resourcesLoaded = true;
        }
    }

    [Fact]
    public void SettingsView_Instantiation_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var v = new SettingsView();
                // 强制 layout 让所有 template/style 评估
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"SettingsView load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    [Fact]
    public void SettingsView_Instantiation_WithLogFile_DoesNotThrow()
    {
        // 同时写一份 stack dump 到磁盘方便复查
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"settingsview-load-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var v = new SettingsView();
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            var msg =
                $"SettingsView load FAILED at {DateTime.Now:O}\n" +
                $"Outer: {caught.GetType().FullName}: {caught.Message}\n" +
                $"Stack:\n{caught.StackTrace}\n" +
                $"--- InnerException ---\n" +
                $"{caught.InnerException?.GetType().FullName}: {caught.InnerException?.Message}\n" +
                $"Inner Stack:\n{caught.InnerException?.StackTrace}\n";
            File.WriteAllText(logPath, msg);
            throw new Exception($"SettingsView load failed — see {logPath}\n{msg}", caught);
        }

        // success 也写一行方便对比
        File.WriteAllText(logPath, $"SettingsView loaded OK at {DateTime.Now:O}");
    }
}