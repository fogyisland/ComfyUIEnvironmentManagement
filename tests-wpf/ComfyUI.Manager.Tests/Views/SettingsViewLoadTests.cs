using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
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

    /// <summary>
    /// v0.6.11+ SDD B T1:dirty 索引器绑定不抛 XAML 解析异常 + toolbar 渲染。
    /// 验 WPF {Binding Dirty[PropertyName]} 索引器绑定求值不会因 INPC key 缺失
    /// 而 throw,toolbar 上 HasUnsavedChanges / UnsavedCount 渲染不出错。
    /// </summary>
    [Fact]
    public void SettingsView_WithDirtyRows_RendersDirtyMarkers()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var vm = new SettingsViewModel(
                    new SettingsRepository(Path.Combine(Path.GetTempPath(),
                        $"settings-dirty-{Guid.NewGuid():N}.json")),
                    GitProxyConfig.Disabled,
                    new FakeValidator());
                vm.DefaultModelsDirectory = "dirty";   // 标 dirty 一行
                Assert.True(vm.HasUnsavedChanges);     // 验 dirty plumbing
                var v = new SettingsView { DataContext = vm };
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"SettingsView dirty-rows load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new ValidationResult(true, "ok"));
    }
}