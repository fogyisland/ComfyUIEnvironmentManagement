using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x #594:启动期路径错位确认弹窗 VM。展示 <see cref="StartupPathProbe"/> 输出的可疑路径列表,
/// 让用户**逐项**确认是否更新到当前 projectRoot + subdir。
///
/// <para>
/// 用户选择语义:
/// - 每项 <see cref="PathMigrationItemViewModel.Selected"/> = true(默认勾选)
///   → 该项的 <see cref="PathMigrationItem.RecommendedValue"/> 被采纳
/// - <c>Selected</c> = false → 保留 <see cref="PathMigrationItem.CurrentValue"/>
/// - 用户勾选/取消在 UI 完成;最终 <see cref="ConfirmCommand"/> 写出 <see cref="Decisions"/>
/// </para>
///
/// 复用 <see cref="NodeInstallDiffWarningViewModel"/> 的 <c>CloseRequested</c> + 结果属性模式:
/// caller 在 <c>ShowDialog()</c> 返回后读取 <see cref="Decisions"/>(null = 用户取消)。
/// </summary>
public sealed class PathMigrationConfirmViewModel : ViewModelBase
{
    public ObservableCollection<PathMigrationItemViewModel> Items { get; } = new();

    /// <summary>
    /// 用户最终决定。<c>null</c> = 用户按了 Cancel/关闭 → 不动 settings。
    /// 非 null → 每项 <c>(Label, CurrentValue, RecommendedValue, Apply)</c>。
    /// </summary>
    public IReadOnlyList<(string Label, string CurrentValue, string RecommendedValue, bool Apply)>? Decisions { get; private set; }

    public string HeaderText => Items.Count switch
    {
        0 => "未发现可疑路径",
        1 => "检测到 1 个路径错位,请确认:",
        _ => $"检测到 {Items.Count} 个路径错位,请逐项确认:",
    };

    public RelayCommand ApplyAllCommand { get; }
    public RelayCommand KeepAllCommand { get; }
    public RelayCommand ToggleAllCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action? CloseRequested;

    public PathMigrationConfirmViewModel(IReadOnlyList<PathMigrationItem> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        foreach (var i in items)
        {
            Items.Add(new PathMigrationItemViewModel(i.Label, i.CurrentValue, i.RecommendedValue));
        }

        ApplyAllCommand = new RelayCommand(
            _ => SetAll(true),
            _ => Items.Count > 0);
        KeepAllCommand = new RelayCommand(
            _ => SetAll(false),
            _ => Items.Count > 0);
        ToggleAllCommand = new RelayCommand(
            _ => { foreach (var it in Items) it.Selected = !it.Selected; },
            _ => Items.Count > 0);
        ConfirmCommand = new RelayCommand(
            _ => Confirm(),
            _ => Items.Count > 0);
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    private void SetAll(bool value)
    {
        foreach (var it in Items) it.Selected = value;
    }

    private void Confirm()
    {
        Decisions = Items
            .Select(i => (Label: i.Label, CurrentValue: i.CurrentValue, RecommendedValue: i.RecommendedValue, Apply: i.Selected))
            .ToList();
        CloseRequested?.Invoke();
    }

    private void Cancel()
    {
        Decisions = null;
        CloseRequested?.Invoke();
    }
}

/// <summary>
/// 弹窗列表的每项 VM。默认勾选 = 应用推荐值;用户可取消勾选保留原值。
/// </summary>
public sealed class PathMigrationItemViewModel : ViewModelBase
{
    public string Label { get; }
    public string CurrentValue { get; }
    public string RecommendedValue { get; }

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            RaisePropertyChanged();
        }
    }

    public PathMigrationItemViewModel(string label, string currentValue, string recommendedValue)
    {
        Label = label ?? "";
        CurrentValue = currentValue ?? "";
        RecommendedValue = recommendedValue ?? "";
    }
}