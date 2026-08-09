using System;
using System.IO;
using System.Threading;
using System.Windows;
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
    [Fact]
    public void SettingsView_Instantiation_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
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
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
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