using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public enum PickerSelectionMode { Single, Multi }

/// <summary>
/// v0.6.6:被 <see cref="Views.BaseEnvProfilePickerDialog"/> 包装的 picker VM。
/// ComboBox 选 torch 版本 → ListBox 过滤出该 torch 下的 CUDA 变体 → 单/多选。
/// 跨 torch 切换自动清空 SelectedProfiles,避免跨版本残留。
/// </summary>
public sealed class BaseEnvProfilePickerViewModel : ViewModelBase
{
    private readonly List<BaseEnvProfile> _allProfiles;
    private IReadOnlyList<BaseEnvProfile> _filteredProfiles = Array.Empty<BaseEnvProfile>();
    private PyTorchVersionEntry? _selectedVersion;
    private IReadOnlyList<BaseEnvProfile> _selectedProfiles = Array.Empty<BaseEnvProfile>();

    public BaseEnvProfilePickerViewModel(
        IReadOnlyList<BaseEnvProfile> profiles,
        BaseEnvProfile? preselected,
        PickerSelectionMode selectionMode)
    {
        if (profiles is null) throw new ArgumentNullException(nameof(profiles));
        SelectionMode = selectionMode;
        _allProfiles = profiles.ToList();

        // torch versions 去重 → ComboBox source。
        Versions = _allProfiles
            .Where(p => p.TorchVersion is not null)
            .Select(p => new PyTorchVersionEntry
            {
                Version = p.TorchVersion!,
                IsNightly = false,
                DisplayName = $"PyTorch {p.TorchVersion}",
            })
            .DistinctBy(e => e.Version)
            .ToList();

        // 默认选第一个 stable torch。
        if (Versions.Count > 0)
        {
            _selectedVersion = Versions[0];
            ApplyFilter();
        }

        if (preselected is not null && _filteredProfiles.Contains(preselected))
        {
            _selectedProfiles = new[] { preselected };
        }

        OkCommand = new RelayCommand(
            _ => Result = SelectedProfiles.ToList(),
            _ => SelectedProfiles.Count >= 1 && (SelectionMode == PickerSelectionMode.Multi || SelectedProfiles.Count == 1));
        CancelCommand = new RelayCommand(_ => Result = null);
    }

    public PickerSelectionMode SelectionMode { get; }

    public IReadOnlyList<PyTorchVersionEntry> Versions { get; }

    public PyTorchVersionEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                _selectedProfiles = Array.Empty<BaseEnvProfile>();
                RaisePropertyChanged(nameof(SelectedProfiles));
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<BaseEnvProfile> Profiles
    {
        get => _filteredProfiles;
        private set => SetField(ref _filteredProfiles, value);
    }

    public IReadOnlyList<BaseEnvProfile> SelectedProfiles
    {
        get => _selectedProfiles;
        set
        {
            if (SelectionMode == PickerSelectionMode.Single && value.Count > 1)
                throw new ArgumentException("Single selection mode 不允许多选", nameof(value));
            SetField(ref _selectedProfiles, value.ToList());
        }
    }

    public IReadOnlyList<BaseEnvProfile>? Result { get; private set; }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void ApplyFilter()
    {
        Profiles = _selectedVersion is null
            ? Array.Empty<BaseEnvProfile>()
            : _allProfiles.Where(p => p.TorchVersion == _selectedVersion.Version).ToList();
    }
}