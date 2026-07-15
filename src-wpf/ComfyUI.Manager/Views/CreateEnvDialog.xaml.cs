using System.Windows;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Microsoft.Win32;

namespace ComfyUI.Manager.Views;

public partial class CreateEnvDialog : Window
{
    public Models.Environment? Result { get; private set; }

    public CreateEnvDialog(CreateEnvDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += result =>
        {
            Result = result;
            DialogResult = result is not null;
            Close();
        };
    }

    public static Models.Environment? Show(EnvCreatorService creator)
    {
        var vm = new CreateEnvDialogViewModel(creator);
        var dlg = new CreateEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private void BrowsePython(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Python 解释器",
            Filter = "python.exe|python.exe;python3.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog() == true &&
            DataContext is CreateEnvDialogViewModel vm)
        {
            vm.PythonExe = dlg.FileName;
        }
    }

    private void BrowseComfyui(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 ComfyUI 源目录",
        };
        if (dlg.ShowDialog() == true &&
            DataContext is CreateEnvDialogViewModel vm)
        {
            vm.ComfyuiSource = dlg.FolderName;
        }
    }
}
