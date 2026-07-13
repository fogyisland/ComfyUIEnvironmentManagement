using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class VersionPanelViewModel : ViewModelBase
{
    private readonly NodeRepository _nodeRepo;
    private readonly string _envId;

    public ObservableCollection<VersionStatus> Versions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand ToggleLockCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public event Func<string, Task>? HistoryRequested;

    public VersionPanelViewModel(NodeRepository nodeRepo, string envId)
    {
        _nodeRepo = nodeRepo;
        _envId = envId;
        RefreshCommand = new RelayCommand(_ => RefreshAll());
        UpgradeCommand = new RelayCommand(
            p => Upgrade(p as VersionStatus ?? Selected),
            p => (p as VersionStatus ?? Selected)?.HasUpdate == true
                && (p as VersionStatus ?? Selected)?.Locked == false);
        ToggleLockCommand = new RelayCommand(
            p => ToggleLock(p as VersionStatus ?? Selected));
        ShowHistoryCommand = new RelayCommand(
            async p => {
                var v = p as VersionStatus ?? Selected;
                if (v is not null && HistoryRequested is not null)
                    await HistoryRequested(v.Package);
            });
        RefreshAll();
    }

    public List<ScannedNode> Packages { get; } = new();
    private string? _selectedPackage;
    public string? SelectedPackage { get => _selectedPackage; set { if (SetField(ref _selectedPackage, value)) RefreshVersions(); } }

    private VersionStatus? _selected;
    public VersionStatus? Selected { get => _selected; set => SetField(ref _selected, value); }

    private void RefreshAll()
    {
        Packages.Clear();
        foreach (var n in _nodeRepo.ListByEnv(_envId)) Packages.Add(n);
        if (Packages.Count > 0 && SelectedPackage is null)
            SelectedPackage = Packages[0].Package;
        else
            RefreshVersions();
    }

    private void RefreshVersions()
    {
        Versions.Clear();
        if (SelectedPackage is null) return;
        var node = Packages.Find(n => n.Package == SelectedPackage);
        if (node is null) return;
        // Real read of stored version state; latest_version / has_update come
        // from a live git compare, which is TODO(M5.2-T7).
        Versions.Add(new VersionStatus
        {
            Package = node.Package,
            CurrentVersion = node.Version ?? "",
            CurrentSha = "",
            CurrentShaShort = "",
            LatestVersion = "",
            HasUpdate = false,
            Locked = node.Locked,
        });
    }

    private void Upgrade(VersionStatus? v)
    {
        if (v is null) return;
        // TODO(M5.2-T7): upgrade package via NodeOperations + GitRunner.
        MessageBox.Show($"TODO(M5.2-T7): upgrade '{v.Package}'", "升级");
    }

    private void ToggleLock(VersionStatus? v)
    {
        if (v is null) return;
        // TODO(M5.2-T7): lock/unlock version via NodeOperations.
        MessageBox.Show($"TODO(M5.2-T7): toggle lock '{v.Package}'", "锁定");
    }
}
