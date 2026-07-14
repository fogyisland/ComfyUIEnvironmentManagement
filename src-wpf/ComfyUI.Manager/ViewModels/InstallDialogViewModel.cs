using System;
using System.Collections.ObjectModel;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class InstallDialogViewModel : ViewModelBase
{
    private readonly EnvironmentRepository _repo;
    private readonly NodeOperations _ops;
    public CatalogEntry Entry { get; }
    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand InstallCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public InstallDialogViewModel(
        EnvironmentRepository repo,
        NodeOperations ops,
        CatalogEntry entry)
    {
        _repo = repo;
        _ops = ops;
        Entry = entry;
        InstallCommand = new RelayCommand(
            async _ => await InstallAsync(),
            _ => SelectedEnv is not null && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        LoadEnvs();
    }

    private Environment? _selectedEnv;
    public Environment? SelectedEnv { get => _selectedEnv; set => SetField(ref _selectedEnv, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { if (SetField(ref _busy, value)) InstallCommand.RaiseCanExecuteChanged(); } }

    private string? _progress;
    public string? Progress { get => _progress; set => SetField(ref _progress, value); }

    private void LoadEnvs()
    {
        Environments.Clear();
        foreach (var e in _repo.ListAll()) Environments.Add(e);
        if (Environments.Count > 0) SelectedEnv = Environments[0];
    }

    private async System.Threading.Tasks.Task InstallAsync()
    {
        if (SelectedEnv is null) return;
        var envId = SelectedEnv.Id;
        // CatalogEntry 没专用字段;从 raw_metadata 拿("repository" / "url")。
        // ComfyUI-Manager catalog 约定:在 raw_metadata 里有 "url" 或 "repository"。
        var repoUrl = ExtractRepoUrl(Entry);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            MessageBox.Show("catalog 条目缺 repository url", "安装节点",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Busy = true;
        Progress = "Cloning...";
        try
        {
            // 用 nodeId = 包名作为目录名(ComfyUI-Manager 约定)。
            var result = await _ops.InstallAsync(envId, Entry.Package, repoUrl);
            if (result.Success)
            {
                Progress = $"OK, version={result.Version}";
                CloseRequested?.Invoke();
            }
            else
            {
                Progress = $"失败:{result.Reason}";
            }
        }
        catch (Exception ex)
        {
            Progress = $"异常:{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }
}
