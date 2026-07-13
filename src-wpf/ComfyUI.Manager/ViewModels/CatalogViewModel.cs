using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private const int SearchLimit = 50;
    private readonly CatalogRepository _repo;

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) Search(); }
    }

    public CatalogViewModel(CatalogRepository repo)
    {
        _repo = repo;
        RefreshCommand = new RelayCommand(_ => Refresh());
        InstallCommand = new RelayCommand(
            p => Install(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        Search();
    }

    private CatalogEntry? _selected;
    public CatalogEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

    private void Search()
    {
        Entries.Clear();
        foreach (var e in _repo.Search(_query, SearchLimit)) Entries.Add(e);
    }

    private void Refresh()
    {
        // TODO(M5.2-T7): refresh catalog from remote registry via NodeOperations.
        MessageBox.Show("TODO(M5.2-T7): refresh catalog", "刷新目录");
        Search();
    }

    private void Install(CatalogEntry? entry)
    {
        if (entry is null) return;
        // TODO(M5.2-T7): install node from catalog via NodeOperations + GitRunner.
        MessageBox.Show(
            $"TODO(M5.2-T7): install '{entry.Package}'", "安装节点");
    }
}
