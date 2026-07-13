using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public enum BulkUpdateMode { SelectEnv, Running, Summary }

public class BulkUpdateDialogViewModel : ViewModelBase
{
    private BulkUpdateSummary? _summary;
    private BulkUpdateMode _mode = BulkUpdateMode.SelectEnv;
    private string? _bulkId;
    private string? _errorMessage;
    private bool _isBusy;

    public ObservableCollection<EnvRow> EnvRows { get; } = new();
    public ObservableCollection<BulkUpdateRow> Rows { get; } = new();
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }

    public BulkUpdateSummary? Summary
    {
        get => _summary;
        set { _summary = value; RaisePropertyChanged(); }
    }

    public BulkUpdateMode Mode
    {
        get => _mode;
        set { _mode = value; RaisePropertyChanged(); }
    }

    public string? BulkId
    {
        get => _bulkId;
        set { _bulkId = value; RaisePropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; RaisePropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); }
    }

    // TODO(M5.2-T6): take a BulkUpdateOrchestrator and drive the run locally.
    // The HTTP BulkUpdateApiClient path was removed with the Python service;
    // the orchestrator is implemented in T6, so for now every operation shows
    // a placeholder message.
    public BulkUpdateDialogViewModel()
    {
        StartCommand = new RelayCommand(_ => Start(), _ => CanStart());
        CancelCommand = new RelayCommand(
            _ => Cancel(),
            _ => BulkId != null && Mode == BulkUpdateMode.Running);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll());
    }

    public void LoadEnvs(IEnumerable<EnvRow> envs)
    {
        EnvRows.Clear();
        foreach (var e in envs) EnvRows.Add(e);
    }

    private void ToggleSelectAll()
    {
        var allSelected = EnvRows.All(e => e.Selected);
        foreach (var e in EnvRows) e.Selected = !allSelected;
        StartCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart() =>
        !IsBusy
        && EnvRows.Any(e => e.Selected)
        && EnvRows.Where(e => e.Selected)
            .SelectMany(e => e.Nodes)
            .Any(n => n.Selected);

    public List<string> SelectedEnvIds() =>
        EnvRows.Where(e => e.Selected).Select(e => e.EnvId).ToList();

    public List<string> SelectedNodeIds() =>
        EnvRows.Where(e => e.Selected)
            .SelectMany(e => e.Nodes)
            .Where(n => n.Selected)
            .Select(n => n.NodeId)
            .Distinct()
            .ToList();

    private void Start()
    {
        // TODO(M5.2-T6): delegate to BulkUpdateOrchestrator.
        MessageBox.Show("批量更新功能待 T6 实现", "批量更新");
    }

    private void Cancel()
    {
        // TODO(M5.2-T6): cancel the orchestrator run.
        MessageBox.Show("批量更新功能待 T6 实现", "批量更新");
    }
}

public class EnvRow : ViewModelBase
{
    private bool _selected;
    public string EnvId { get; }
    public string DisplayName { get; }
    public ObservableCollection<NodeSelectRow> Nodes { get; } = new();
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public EnvRow(string envId, string displayName)
    {
        EnvId = envId;
        DisplayName = displayName;
    }
}

public class NodeSelectRow : ViewModelBase
{
    private bool _selected;
    public string NodeId { get; }
    public string DisplayName { get; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; RaisePropertyChanged(); }
    }
    public NodeSelectRow(string nodeId, string displayName)
    {
        NodeId = nodeId;
        DisplayName = displayName;
    }
}
