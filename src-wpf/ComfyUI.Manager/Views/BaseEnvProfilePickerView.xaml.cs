using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class BaseEnvProfilePickerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(BaseEnvProfilePickerViewModel), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(PickerSelectionMode), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(PickerSelectionMode.Multi, (d, _) => ((BaseEnvProfilePickerView)d).ApplySelectionMode()));

    public static readonly DependencyProperty SelectedProfilesProperty = DependencyProperty.Register(
        nameof(SelectedProfiles), typeof(IReadOnlyList<BaseEnvProfile>), typeof(BaseEnvProfilePickerView),
        new PropertyMetadata(Array.Empty<BaseEnvProfile>(), (d, _) => ((BaseEnvProfilePickerView)d).ApplySelectedProfiles()));

    public BaseEnvProfilePickerView()
    {
        InitializeComponent();
        ApplySelectionMode();
    }

    public BaseEnvProfilePickerViewModel? ViewModel
    {
        get => (BaseEnvProfilePickerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PickerSelectionMode SelectionMode
    {
        get => (PickerSelectionMode)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public IReadOnlyList<BaseEnvProfile> SelectedProfiles
    {
        get => (IReadOnlyList<BaseEnvProfile>)GetValue(SelectedProfilesProperty);
        set => SetValue(SelectedProfilesProperty, value);
    }

    /// <summary>测试 seam:让测试访问 ListBox 实例来断言 SelectedItems 同步。</summary>
    public ListBox ProfileListBox => _profileListBox ??= (ListBox)FindName("ProfileListBoxControl");
    private ListBox? _profileListBox;

    /// <summary>
    /// reentry guard:<see cref="ApplySelectedProfiles"/> 跑批量 Clear/Add 时
    /// ListBox 会触发 <see cref="OnListBoxSelectionChanged"/>,后者又会 SetValue
    /// DP → 再次跑 ApplySelectedProfiles 形成无限重入。flag 在 ApplySelectedProfiles
    /// 运行期间屏蔽 selection 回调的写回,只在外部触发(用户真实点击)时同步。
    /// </summary>
    private bool _suppressSelectionSync;

    private void ApplySelectionMode()
    {
        if (ProfileListBox is null) return;
        ProfileListBox.SelectionMode = SelectionMode == PickerSelectionMode.Single
            ? System.Windows.Controls.SelectionMode.Single
            : System.Windows.Controls.SelectionMode.Extended;
    }

    private void ApplySelectedProfiles()
    {
        if (ProfileListBox is null || ViewModel is null) return;
        // 强制 layout 让 ItemsSource binding 把 ViewModel.Profiles 推到 ListBox.Items。
        ProfileListBox.UpdateLayout();
        _suppressSelectionSync = true;
        try
        {
            // WPF 在 SelectionMode.Single 下禁止改 SelectedItems 集合
            // (InvalidOperationException"只能在多选模式中更改 SelectedItems 集合"),
            // 必须走 SelectedItem。v0.6.6 T3 的 Single 模式 + 预选路径踩到过。
            if (ProfileListBox.SelectionMode == System.Windows.Controls.SelectionMode.Single)
            {
                var first = SelectedProfiles.FirstOrDefault(p => ProfileListBox.Items.Contains(p));
                ProfileListBox.SelectedItem = first;
            }
            else
            {
                ProfileListBox.SelectedItems.Clear();
                foreach (var p in SelectedProfiles)
                {
                    if (ProfileListBox.Items.Contains(p)) ProfileListBox.SelectedItems.Add(p);
                }
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
        ViewModel.SelectedProfiles = SelectedProfiles;
    }

    private void OnListBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || _suppressSelectionSync) return;
        var selected = ProfileListBox.SelectedItems.Cast<BaseEnvProfile>().ToList();
        try
        {
            ViewModel.SelectedProfiles = selected;
            SetValue(SelectedProfilesProperty, selected);
        }
        catch (ArgumentException)
        {
            // Single mode 下用户用 Ctrl+Click 多选 → VM 抛异常,UI 状态回滚到第一项。
            // 注意:此分支只在 Single 模式发生,不能碰 SelectedItems 集合(WPF 禁止)。
            _suppressSelectionSync = true;
            try
            {
                if (ProfileListBox.SelectionMode == System.Windows.Controls.SelectionMode.Single)
                {
                    ProfileListBox.SelectedItem = selected.Count > 0 ? selected[0] : null;
                }
                else
                {
                    ProfileListBox.SelectedItems.Clear();
                    if (selected.Count > 0) ProfileListBox.SelectedItems.Add(selected[0]);
                }
            }
            finally
            {
                _suppressSelectionSync = false;
            }
            ViewModel.SelectedProfiles = ProfileListBox.SelectedItems.Cast<BaseEnvProfile>().ToList();
            SetValue(SelectedProfilesProperty, ViewModel.SelectedProfiles);
        }
    }
}
