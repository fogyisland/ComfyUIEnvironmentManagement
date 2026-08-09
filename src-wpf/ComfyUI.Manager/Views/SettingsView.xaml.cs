using System;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SyncTokenFromViewModel();
            SyncSectionScrollSubscription();
        };
    }

    private SettingsViewModel? _vm;
    private bool _scrollSubscribed;

    /// <summary>
    /// v0.6.9 T7:订阅 VM 的 SectionScrollRequested event。DataContext 变化时重新订阅
    /// (MainViewModel 缓存同一份 VM,所以正常情况只会订阅一次,但重置保险)。
    /// </summary>
    private void SyncSectionScrollSubscription()
    {
        if (_vm is not null && _scrollSubscribed)
        {
            _vm.SectionScrollRequested -= OnSectionScrollRequested;
            _scrollSubscribed = false;
        }
        if (DataContext is SettingsViewModel vm)
        {
            _vm = vm;
            vm.SectionScrollRequested += OnSectionScrollRequested;
            _scrollSubscribed = true;
        }
    }

    private void OnSectionScrollRequested(object? sender, string sectionKey)
    {
        // 找 x:Name="Section{sectionKey}" 的 TextBlock,HitTest / ScrollIntoView。
        // FindName 在 UserControl 上找 — 必须先用 UpdateLayout 让 x:Name 注册进来。
        if (string.IsNullOrEmpty(sectionKey)) return;
        UpdateLayout();
        var element = FindName($"Section{sectionKey}") as FrameworkElement;
        element?.BringIntoView();
    }

    private void SyncTokenFromViewModel()
    {
        // PasswordBox 不参与 XAML 双向绑定(string 会明文显示),
        // 首次加载时把 VM 里已存的 token 灌进 PasswordBox。
        if (DataContext is SettingsViewModel vm && GitHubTokenBox.Password != vm.GitHubToken)
        {
            GitHubTokenBox.Password = vm.GitHubToken;
        }
    }

    private void OnGitHubTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
        {
            vm.GitHubToken = pb.Password;
        }
    }

    private void BrowseTemplateComfyui(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.TemplateComfyuiDir = picked;
        }
    }

    private void BrowseEnvsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.EnvsDir = picked;
        }
    }

    private void BrowseGlobalNodesDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.GlobalNodesDir = picked;
        }
    }

    private void BrowseLocalNodeDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.LocalNodeDirectory = picked;
        }
    }

    private void BrowseSharedModelsDirectory(object sender, RoutedEventArgs e)
    {
        var picked = DataContext is SettingsViewModel vm ? vm.PickFolder() : null;
        if (!string.IsNullOrEmpty(picked) && DataContext is SettingsViewModel vm2)
        {
            vm2.SharedModelsDirectory = picked;
        }
    }

    private void BrowsePythonVenvBaseline(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("Python 解释器", "python.exe|python.exe;python3.exe|所有文件|*.*");
            if (picked is not null) vm.PythonVenvBaseline = picked;
        }
    }

    private void BrowseGitExe(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("git.exe", "git.exe|git.exe|所有文件|*.*");
            if (picked is not null) vm.GitExe = picked;
        }
    }

    private void BrowsePythonInterpreter(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("选择 Python 解释器", "可执行文件|*.exe|所有文件|*.*");
            if (picked is not null) vm.NewPythonInterpreterPath = picked;
        }
    }

    private void BrowseExtraPath(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExtraPath ep } && DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) ep.Path = picked;
        }
    }
}
