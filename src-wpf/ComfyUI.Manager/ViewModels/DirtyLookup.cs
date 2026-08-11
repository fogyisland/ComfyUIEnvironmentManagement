using System.Collections.Generic;
using System.ComponentModel;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.11+ SDD B T1:per-property dirty 标记集合,暴露索引器供 XAML 绑定
/// <c>{Binding Dirty[PropertyName]}</c>。WPF 索引器绑定约定 key 是 "Item[]",
/// 标 dirty 时 raise "Item[]" 让所有索引器绑定重算。
///
/// 线程模型:单 UI 线程访问,无锁。
/// </summary>
public sealed class DirtyLookup : INotifyPropertyChanged
{
    private readonly HashSet<string> _dirty = new(System.StringComparer.Ordinal);

    /// <summary>
    /// 给定 property 名字面量是否 dirty。XAML 写 <c>Dirty[PropertyName]</c>。
    /// </summary>
    public bool this[string propertyName] =>
        !string.IsNullOrEmpty(propertyName) && _dirty.Contains(propertyName);

    public int Count => _dirty.Count;
    public bool Any => _dirty.Count > 0;

    public void Mark(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;
        if (!_dirty.Add(propertyName)) return;     // 已 dirty → no-op 不 notify
        RaiseAll();
    }

    public void Clear()
    {
        if (_dirty.Count == 0) return;
        _dirty.Clear();
        RaiseAll();
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Any)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}