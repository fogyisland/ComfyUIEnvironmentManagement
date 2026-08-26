using System;
using System.Windows;
using ComfyUI.Manager.ViewModels;
using Microsoft.Win32;

namespace ComfyUI.Manager.Views;

public partial class EditLocalPathDialog : Window
{
    public bool Confirmed { get; private set; }
    public string? ResultPath { get; private set; }

    public EditLocalPathDialog(EditLocalPathDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += (ok, path) =>
        {
            Confirmed = ok;
            ResultPath = path;
            DialogResult = ok;
            Close();
        };
        // 焦点给到 TextBox 方便用户直接打字
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // 用 OpenFileDialog 但用户可手输 — dialog 提供「文件存在才接受」门槛,
        // 这里只 initial directory 给当前 path 文本。文件夹场景(WIFU 之类模型以
        // 目录为单位)允许 dialog 选文件再剥父目录;实在不行用户手输绝对路径。
        var vm = (EditLocalPathDialogViewModel)DataContext;
        var dlg = new OpenFileDialog
        {
            Title = "选择本地模型文件",
            CheckFileExists = false,
            ValidateNames = false,
        };
        if (!string.IsNullOrWhiteSpace(vm.Path))
        {
            var dir = System.IO.Path.GetDirectoryName(vm.Path);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                dlg.InitialDirectory = dir;
            }
        }
        if (dlg.ShowDialog() == true)
        {
            vm.Path = dlg.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 委托给 VM 的 ConfirmCommand (canExecute 守卫 Directory.Exists 之类)
        var vm = (EditLocalPathDialogViewModel)DataContext;
        if (vm.ConfirmCommand.CanExecute(null))
        {
            vm.ConfirmCommand.Execute(null);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // vm.CancelCommand → Closed(false, null) → DialogResult=false → Close
        var vm = (EditLocalPathDialogViewModel)DataContext;
        vm.CancelCommand.Execute(null);
    }
}