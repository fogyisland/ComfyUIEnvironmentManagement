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

    /// <summary>
    /// v0.6.12 T4:SettingsView 加了 LogDirectory 行(label + TextBox + hint)后
    /// XAML 解析不抛。按 v0.6.9.2 `1945b4b` 教训:任何新 XAML 元素含 Theme Setter
    /// binding 都可能让 XamlParseException 复发,所以每个新增 row 配套一个 STA load test。
    /// </summary>
    [Fact]
    public void SettingsView_WithLogDirectoryRow_LoadsWithoutCrash()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var vm = new SettingsViewModel(
                    new SettingsRepository(Path.Combine(Path.GetTempPath(),
                        $"settings-logdir-{Guid.NewGuid():N}.json")),
                    GitProxyConfig.Disabled,
                    new FakeValidator());
                vm.LogDirectory = @"D:\my-logs";  // 标 dirty 一行让 Dirty binding 也走一遍
                Assert.True(vm.HasUnsavedChanges);
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
                $"SettingsView LogDirectory row load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    /// <summary>
    /// v0.6.14.1 hotfix:SettingsView 加载时 SyncTokenFromViewModel 把 VM 里的
    /// GitHubToken 推到 PasswordBox,但 PasswordBox.Password = X 会触发
    /// PasswordChanged → OnGitHubTokenChanged → VM 调 MarkDirty("GitHubToken")
    /// → 每次打开 Settings 都显示 ⚠"尚未保存"(用户报告)。期望:PasswordBox 灌入
    /// 之后 vm.Dirty["GitHubToken"] 必须为 false。
    /// </summary>
    [Fact]
    public void SettingsView_WithPresetGitHubToken_DataContextSet_DoesNotMarkDirty()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                // 模拟"用户已经保存过 token":Settings 实例直接持有 token
                var shared = new ComfyUI.Manager.Models.Settings
                {
                    GitHubToken = "ghp_test_preexisting",
                };
                var repo = new SettingsRepository(Path.Combine(Path.GetTempPath(),
                    $"settings-token-{Guid.NewGuid():N}.json"));
                var vm = new SettingsViewModel(repo, GitProxyConfig.Disabled,
                    new FakeValidator(), sharedSettings: shared);
                Assert.Equal("ghp_test_preexisting", vm.GitHubToken);
                Assert.False(vm.Dirty["GitHubToken"]);  // 刚构造,没动过

                // 设 DataContext → 触发 SettingsView.DataContextChanged →
                // SyncTokenFromViewModel → PasswordBox.Password = vm.GitHubToken
                var v = new SettingsView { DataContext = vm };
                v.Measure(new Size(800, 600));
                v.Arrange(new Rect(0, 0, 800, 600));
                v.UpdateLayout();

                // 关键断言:PasswordBox 灌入后,VM 不应被标 dirty
                Assert.Equal("ghp_test_preexisting",
                    v.GitHubTokenBox.Password);  // 确认 sync 真的跑了
                Assert.False(vm.Dirty["GitHubToken"],
                    "SyncTokenFromViewModel 不应把 GitHubToken 标 dirty(回环 bug)");
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"SettingsView GitHubToken dirty-guard test failed: " +
                $"{caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new ValidationResult(true, "ok"));
    }
}