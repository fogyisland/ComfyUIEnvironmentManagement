using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15:EnvPickerDialog 弹窗时使用的简化 env 记录。只装 Id + Name,够 UI 列表展示。
/// </summary>
public sealed record EnvOption(string Id, string Name);

/// <summary>
/// v0.6.15:本地节点 → 复制到 env 时的 env 选择 dialog VM。
/// Closed event:OK 返 SelectedEnv;Cancel 返 null。
/// </summary>
public class EnvPickerDialogViewModel : ViewModelBase
{
    public ObservableCollection<EnvOption> Environments { get; }

    private EnvOption? _selected;
    public EnvOption? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    /// <summary>关闭时 fire 一次:OK 返 SelectedEnv,Cancel 返 null。</summary>
    public event Action<EnvOption?>? Closed;

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public EnvPickerDialogViewModel(IList<EnvOption> envs)
    {
        Environments = new ObservableCollection<EnvOption>(envs);
        // 默认选第一个
        _selected = Environments.FirstOrDefault();
        OkCommand = new RelayCommand(_ => Closed?.Invoke(Selected), _ => Selected is not null);
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
    }
}