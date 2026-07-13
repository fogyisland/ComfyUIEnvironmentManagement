using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class DepPanelViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly DepRepository _depRepo;
    private readonly string _envId;

    public ObservableCollection<DepRecord> Deps { get; } = new();
    public ObservableCollection<string> Conflicts { get; } = new();
    public List<ScannedNode> Packages { get; } = new();
    public RelayCommand ScanCommand { get; }
    public RelayCommand LocalCheckCommand { get; }
    public RelayCommand GlobalCheckCommand { get; }

    public DepPanelViewModel(
        NodeRepository nodeRepo, DepRepository depRepo, string envId)
    {
        _nodeRepo = nodeRepo;
        _depRepo = depRepo;
        _envId = envId;
        ScanCommand = new RelayCommand(_ => Scan());
        LocalCheckCommand = new RelayCommand(_ => LocalCheck());
        GlobalCheckCommand = new RelayCommand(_ => GlobalCheck());
        LoadPackages();
    }

    private string? _selectedPackage;
    public string? SelectedPackage { get => _selectedPackage; set { if (SetField(ref _selectedPackage, value)) LoadDeps(); } }

    private void LoadPackages()
    {
        Packages.Clear();
        foreach (var n in _nodeRepo.ListByEnv(_envId)) Packages.Add(n);
        if (Packages.Count > 0 && SelectedPackage is null)
            SelectedPackage = Packages[0].Package;
    }

    private void LoadDeps()
    {
        Deps.Clear();
        if (SelectedPackage is null) return;
        foreach (var d in _depRepo.ListByEnvAndPackage(_envId, SelectedPackage))
            Deps.Add(d);
    }

    private void Scan()
    {
        // TODO(M5.2-T7): scan package deps via NodeOperations.
        MessageBox.Show("TODO(M5.2-T7): scan deps", "扫描依赖");
        LoadDeps();
    }

    private void LocalCheck()
    {
        // TODO(M5.2-T7): detect local dep conflicts via NodeOperations.
        MessageBox.Show("TODO(M5.2-T7): local conflict check", "本地检查");
    }

    private void GlobalCheck()
    {
        // TODO(M5.2-T7): check global compat via NodeOperations.
        MessageBox.Show("TODO(M5.2-T7): global compat check", "全局检查");
    }
}
