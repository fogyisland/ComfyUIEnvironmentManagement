using System.Windows;
using ComfyUI.Manager.ViewModels.FirstRunWizard;
using Microsoft.Win32;       // WPF OpenFolderDialog (NET 8)

namespace ComfyUI.Manager.Views.FirstRunWizard;

public partial class FirstRunWizardWindow : Window
{
    private readonly FirstRunWizardViewModel _vm;

    public FirstRunWizardWindow(FirstRunWizardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
        vm.Cancelled += () => { DialogResult = false; Close(); };
    }

    private void OnBrowseInstallPath(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择安装根目录",
            InitialDirectory = _vm.InstallPath is { Length: > 0 } ? _vm.InstallPath : null,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.InstallPath = dlg.FolderName;
        }
    }

    private void OnBrowsePythonPath(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 python.exe",
            Filter = "Python 解释器|python.exe;python3.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.PythonPath = dlg.FileName;
        }
    }

    // v1.0.0.x (2026-09-05):Git path Browse handler — 用户可改到系统 PATH "git" 或别处安装的 git.exe。
    // 留空时 wizard 完成会写空字符串 → ResolveGitExe fallback 到 PATH "git"。
    private void OnBrowseGitPath(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 git.exe",
            Filter = "Git 程序|git.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.GitPath = dlg.FileName;
        }
    }

    // v1.0.0.x (2026-09-03) T34:8 个 path 字段 Browse handler(都用 OpenFolderDialog,InitialDirectory
    // fallback 到 Environment.CurrentDirectory = projectRoot,跟 SettingsDefaults.Apply seed 一致)

    private void OnBrowseSystemTemplateLibraryDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择系统模板库目录 (clone 模板源码)",
            InitialDirectory = DirToBrowse(_vm.SystemTemplateLibraryDir),
        };
        if (dlg.ShowDialog(this) == true) _vm.SystemTemplateLibraryDir = dlg.FolderName;
    }

    private void OnBrowseEnvsDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 Envs 目录 (用户创建的 env)",
            InitialDirectory = DirToBrowse(_vm.EnvsDir),
        };
        if (dlg.ShowDialog(this) == true) _vm.EnvsDir = dlg.FolderName;
    }

    private void OnBrowseGlobalNodesDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 Global Nodes 目录 (全局节点)",
            InitialDirectory = DirToBrowse(_vm.GlobalNodesDir),
        };
        if (dlg.ShowDialog(this) == true) _vm.GlobalNodesDir = dlg.FolderName;
    }

    private void OnBrowseLocalNodeDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 LocalNode 目录 (单节点下载)",
            InitialDirectory = DirToBrowse(_vm.LocalNodeDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.LocalNodeDirectory = dlg.FolderName;
    }

    private void OnBrowseLocalNodesDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 LocalNodes 目录 (批量节点源)",
            InitialDirectory = DirToBrowse(_vm.LocalNodesDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.LocalNodesDirectory = dlg.FolderName;
    }

    private void OnBrowseDefaultModelsDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 Models 目录 (env-create junction + ModelMarketplace)",
            InitialDirectory = DirToBrowse(_vm.DefaultModelsDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.DefaultModelsDirectory = dlg.FolderName;
    }

    private void OnBrowseWorkflowsDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 Workflow 目录 (env-start sync + WorkflowMarketplace)",
            InitialDirectory = DirToBrowse(_vm.WorkflowsDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.WorkflowsDirectory = dlg.FolderName;
    }

    private void OnBrowseLogDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 Logs 父目录 (EnvStartStatus 日志写这里)",
            InitialDirectory = DirToBrowse(_vm.LogDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.LogDirectory = dlg.FolderName;
    }

    /// <summary>
    /// v1.0.0.x (2026-09-03) T34:resolve Browse 对话框 InitialDirectory ——
    /// VM property 已填的 path 用 VM path(用户能从已有路径开始 browse),否则 fallback
    /// 到 Environment.CurrentDirectory = projectRoot(default seed 跟 SettingsDefaults 一致)。
    /// </summary>
    private static string? DirToBrowse(string? vmPath)
    {
        if (!string.IsNullOrWhiteSpace(vmPath) && System.IO.Directory.Exists(vmPath))
            return vmPath;
        return System.Environment.CurrentDirectory;
    }
}
