using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// CatalogEntryPickerViewModel:用于"在 env 行点安装节点 → 弹 catalog 条目选择 dialog"。
///
/// 搜索:每次 Query 变更触发 CatalogRepository.Search(query, limit=200)。
/// 初始空 Query → 列出 limit 个;用户边输边过滤。
/// 选条目按"安装"按钮 / 双击 ListBox → CloseRequested fire。
/// </summary>
public class CatalogEntryPickerViewModel : ViewModelBase
{
    private readonly CatalogRepository _repo;

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand SearchCommand { get; }
    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action? CloseRequested;

    public CatalogEntryPickerViewModel(CatalogRepository repo)
    {
        _repo = repo;
        SearchCommand = new RelayCommand(_ => Refresh());
        OkCommand = new RelayCommand(_ => CloseRequested?.Invoke(), _ => Selected is not null);
        CancelCommand = new RelayCommand(_ =>
        {
            Selected = null;
            CloseRequested?.Invoke();
        });
        Refresh();
    }

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value)) Refresh();
        }
    }

    private CatalogEntry? _selected;
    public CatalogEntry? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                OkCommand.RaiseCanExecuteChanged();
        }
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in _repo.Search(_query ?? "", limit: 200)) Entries.Add(e);
        // 保持现有选中如果还在新结果里
        if (Selected is not null)
        {
            var match = Entries.FirstOrDefault(e => e.Id == Selected.Id);
            Selected = match;
        }
    }
}