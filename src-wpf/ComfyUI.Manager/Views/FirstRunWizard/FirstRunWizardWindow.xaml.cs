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
}
