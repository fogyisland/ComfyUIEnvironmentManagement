using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>
    /// 预填 env:从 EnvironmentList 行点"安装节点" → 走 CatalogEntryPicker → InstallDialog,
    /// 想直接装到当前 env,不是让用户从所有 env 里再选一次。null = 不预填,默认选列表第一条。
    /// </summary>
    public string? PreselectedEnvId { get; }

    public InstallDialogViewModel(
        EnvironmentRepository repo,
        NodeOperations ops,
        CatalogEntry entry,
        string? preselectedEnvId = null)
    {
        _repo = repo;
        _ops = ops;
        Entry = entry;
        PreselectedEnvId = preselectedEnvId;
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
        // 优先用 PreselectedEnvId(从 EnvironmentList 行点"安装节点"过来),
        // 否则默认第一条
        if (!string.IsNullOrEmpty(PreselectedEnvId))
        {
            var match = Environments.FirstOrDefault(e => e.Id == PreselectedEnvId);
            if (match is not null)
            {
                SelectedEnv = match;
                return;
            }
        }
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
            // v0.6.7.5: 传 catalog PipRequirements 让 NodeOperations 在 clone 前
            // 跑 pip list diff,如有 Downgrade/Conflict 弹 modal 让用户确认是否继续。
            // 既有非 catalog 节点安装入口不传 catalogPipReqs → 走原路径不跑 diff。
            var result = await _ops.InstallAsync(
                envId, Entry.Package, repoUrl,
                targetTag: null,
                catalogPipReqs: Entry.PipRequirements,
                ct: default);
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
