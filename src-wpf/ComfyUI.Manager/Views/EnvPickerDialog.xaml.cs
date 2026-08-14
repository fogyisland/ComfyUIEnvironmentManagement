using System;
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class EnvPickerDialog : Window
{
    /// <summary>
    /// 测试 seam:单测可注入只返 stub EnvOption 的函数,避开 WPF 弹窗。
    /// </summary>
    public static Func<string, List<EnvOption>, EnvOption?>? ShowOverride { get; set; }

    public string TitleText { get; }

    public EnvPickerDialog(EnvPickerDialogViewModel vm, string title)
    {
        InitializeComponent();
        DataContext = vm;
        TitleText = title;
        vm.Closed += result =>
        {
            DialogResult = result is not null;
            Close();
        };
    }

    public static EnvOption? Show(string title, List<EnvOption> envs)
    {
        if (ShowOverride is not null) return ShowOverride(title, envs);
        var vm = new EnvPickerDialogViewModel(envs);
        var dlg = new EnvPickerDialog(vm, title) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return vm.Selected;  // Closed event 触发后 vm.Selected 仍保留用户选的
    }
}