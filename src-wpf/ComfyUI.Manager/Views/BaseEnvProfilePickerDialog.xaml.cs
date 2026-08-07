using System;
using System.Collections.Generic;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerDialog : Window
{
    /// <summary>
    /// 测试 seam:生产代码 ShowDialog 弹 WPF Window 阻塞 UI 线程;
    /// 单测可赋值 ShowOverride 模拟用户选择或取消。
    /// </summary>
    public static Func<
        IReadOnlyList<BaseEnvProfile>,
        BaseEnvProfile?,
        PickerSelectionMode,
        IReadOnlyList<BaseEnvProfile>?>? ShowOverride { get; set; }

    private readonly BaseEnvProfilePickerViewModel _vm;

    public BaseEnvProfilePickerDialog(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode)
    {
        InitializeComponent();
        _vm = new BaseEnvProfilePickerViewModel(profiles, preselected, mode);
        Picker.ViewModel = _vm;
        Picker.SelectionMode = mode;
        if (preselected is not null) Picker.SelectedProfiles = new[] { preselected };

        // OK 按钮 enable/disable 跟 OkCommand.CanExecute 联动。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.SelectedProfiles))
            {
                OkButton.IsEnabled = _vm.OkCommand.CanExecute(null);
                if (_vm.SelectedProfiles.Count > 0)
                {
                    HintTextBlock.Text = $"已选 {_vm.SelectedProfiles.Count} 个 profile";
                }
                else
                {
                    HintTextBlock.Text = mode == PickerSelectionMode.Single
                        ? "请选择 1 个 profile"
                        : "请选择至少 1 个 profile";
                }
            }
        };
    }

    /// <summary>
    /// 弹 picker dialog,返回选中 profile 列表。
    /// 用户取消 → 返回 null;无可用 profile → 弹 MessageBox + 返回 null。
    /// </summary>
    public static IReadOnlyList<BaseEnvProfile>? Show(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode mode)
    {
        if (profiles is null || profiles.Count == 0)
        {
            MessageBox.Show("无可用 profile,无法部署", "基础环境部署",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (ShowOverride is not null)
            return ShowOverride(profiles, preselected, mode);

        var dlg = new BaseEnvProfilePickerDialog(profiles, preselected, mode)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true ? dlg._vm.Result : null;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.OkCommand.CanExecute(null))
        {
            MessageBox.Show(
                _vm.SelectionMode == PickerSelectionMode.Single
                    ? "请选择 1 个 profile"
                    : "请选择至少 1 个 profile",
                "未选择",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _vm.OkCommand.Execute(null);
        DialogResult = _vm.Result is not null;
        if (DialogResult != true) Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _vm.CancelCommand.Execute(null);
        DialogResult = false;
        Close();
    }
}
