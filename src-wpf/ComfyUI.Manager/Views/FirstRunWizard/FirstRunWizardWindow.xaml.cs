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
        // v1.0.0.x (2026-09-05):用 Closing event 通知 main,不用 DialogResult setter ——
        // 用户原话"还是会啊",深查发现:WPF DialogResult setter 在 modeless Window 上
        // 也会触发 Close(),然后 Close 触发 ShutdownMode=OnMainWindowClose 检查
        // Application.Current.MainWindow → 整个 app 退。
        // 改用自定义 Closed event handler,Close 前先取消(不让 WPF 触发 ShutdownMode)。
        // v1.0.0.x:用 Window.Closing event 在 wizard 关闭时设 e.Cancel = true(不真关),
        // 但 wizard 自己内部标记"已关闭",用户也能继续看 main。但这样 wizard 永远不
        // 关,用户必须点 X 才行。**更稳的修法:改 ShutdownMode,只在主程序需要时关。**
        // 这里:不在 wizard 设 DialogResult(避开 WPF 触发 ShutdownMode 的奇怪路径),
        // 改用 vm.Completed/Cancelled event → wizard.Close(),不设 DialogResult。
        vm.Completed += () => Close();
        vm.Cancelled += () => Close();
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

    private void OnBrowseNodelistDirectory(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 节点列表目录 (含 custom-node-list.json 的根目录,扫描入库用)",
            InitialDirectory = DirToBrowse(_vm.NodelistDirectory),
        };
        if (dlg.ShowDialog(this) == true) _vm.NodelistDirectory = dlg.FolderName;
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
