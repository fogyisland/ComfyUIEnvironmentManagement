using System;
using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x: 编辑某张 LocalModelCard 的本地绝对路径覆盖。
/// 用户点卡片 [📁 编辑路径] 按钮 → 弹此 modal,确认后通过
/// <see cref="LocalModelsViewModel.SetOverridePath"/> 写 DB + 更新 card。
/// 空输入 + 勾选「恢复默认」= 删除 override;否则 upsert 新路径。
/// </summary>
public class EditLocalPathDialogViewModel : ViewModelBase
{
    private string _path;
    /// <summary>用户编辑中的路径文本(初始 = 当前 override ?? card FullPath)。</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            _path = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    private bool _clearOverride;
    /// <summary>勾选「恢复默认」→ 确认时删 override,UI 走 scanner FullPath。</summary>
    public bool ClearOverride
    {
        get => _clearOverride;
        set
        {
            if (_clearOverride == value) return;
            _clearOverride = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    public LocalModelCard Card { get; }
    /// <summary>scanner 默认 FullPath(展示用,让用户看到「恢复默认」会回到哪)。</summary>
    public string DefaultFullPath { get; }

    public bool CanConfirm => ClearOverride
        || (!string.IsNullOrWhiteSpace(Path) && Path != DefaultFullPath && Directory.Exists(Path));

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action<bool, string?>? Closed;  // (confirmed, overridePath or null when clear)

    public EditLocalPathDialogViewModel(LocalModelCard card, string defaultFullPath)
    {
        Card = card;
        DefaultFullPath = defaultFullPath;
        _path = card.LocalPathOverride ?? defaultFullPath;
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(false, null));
    }

    private void Confirm()
    {
        if (!CanConfirm) return;
        var result = ClearOverride ? null : Path.Trim();
        Closed?.Invoke(true, result);
    }
}