using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class EnvironmentDetailViewModel : ViewModelBase
{
    private readonly NodeRepository _repo;
    private readonly string _envId;

    public ObservableCollection<ScannedNode> Nodes { get; } = new();
    public RelayCommand RescanCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public EnvironmentDetailViewModel(NodeRepository repo, string envId)
    {
        _repo = repo;
        _envId = envId;
        RescanCommand = new RelayCommand(_ => Rescan());
        ToggleCommand = new RelayCommand(
            p => Toggle(p as ScannedNode ?? Selected),
            p => (p as ScannedNode ?? Selected) is not null);
        Load();
    }

    private ScannedNode? _selected;
    public ScannedNode? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    private void Load()
    {
        Nodes.Clear();
        foreach (var n in _repo.ListByEnv(_envId)) Nodes.Add(n);
    }

    private void Rescan()
    {
        // TODO(M5.2-T7): trigger local node rescan via NodeOperations.
        MessageBox.Show(
            "TODO(M5.2-T7): rescan nodes", "重新扫描");
    }

    private void Toggle(ScannedNode? node)
    {
        if (node is null) return;
        // TODO(M5.2-T7): enable/disable node in env via NodeOperations.
        MessageBox.Show(
            $"TODO(M5.2-T7): toggle node '{node.Package}'", "启用/禁用");
    }
}
