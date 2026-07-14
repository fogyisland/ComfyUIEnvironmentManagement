using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private const int SearchLimit = 50;
    private readonly CatalogRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) Search(); }
    }

    public CatalogViewModel(
        CatalogRepository repo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps)
    {
        _repo = repo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        RefreshCommand = new RelayCommand(_ => Refresh());
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
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

    private async System.Threading.Tasks.Task InstallAsync(CatalogEntry? entry)
    {
        if (entry is null) return;
        var repoUrl = ExtractRepoUrl(entry);
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            MessageBox.Show("catalog 条目缺 repository url", "安装节点",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var envs = _envRepo.ListAll();
        if (envs.Count == 0)
        {
            MessageBox.Show("没有 env 可安装,先创建一个", "安装节点",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 简单策略:装到第一个 env。如果以后需要多选,改为 dialog。
        var env = envs[0];
        try
        {
            var result = await _nodeOps.InstallAsync(env.Id, entry.Package, repoUrl);
            MessageBox.Show(
                result.Success
                    ? $"OK, version={result.Version}"
                    : $"失败:{result.Reason}",
                "安装节点",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"异常:{ex.Message}", "安装节点",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
