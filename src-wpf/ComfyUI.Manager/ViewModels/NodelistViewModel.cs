using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-redesign:Settings → 节点列表目录 UI VM
///
/// 用户原话:
/// 1. 节点目录按钮:完整入库 / 增量入库
/// 2. 提取内容建 SQLite 表 NodesList + Nodesdetail(一对多)
/// 3. 直接加载数据库展示所有节点
/// 4. 默认节点 json 放 nodelist 目录,每次从
///    https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json 拉所有文件
/// 5. 增量入库:解析文件夹下 json 写 author + repo_name 到 NodesList
/// 6. 调 author + repo_name 去云端查详细
/// 7. 写 NodesList + NodesDetailed(一对多)
/// 8. 一天一次增量入库,用户可手动点按钮
///
/// 简化:只保留"增量入库"按钮 + "下载 + 入库"组合。
/// </summary>
public sealed class NodelistViewModel : ViewModelBase
{
    private readonly NodelistDownloader _downloader;
    private readonly NodelistIngestor _ingestor;
    private readonly NodelistRepository _repo;

    public ObservableCollection<EntryRow> Entries { get; } = new();

    /// <summary>每个 entry 关联的 details(版本列表)— 嵌套展示</summary>
    public ObservableCollection<DetailRow> Details { get; } = new();

    private string _nodelistDirectory = "";
    public string NodelistDirectory
    {
        get => _nodelistDirectory;
        set { _nodelistDirectory = value ?? ""; RaisePropertyChanged(); }
    }

    private string _host = "https://api.github.com";
    public string Host
    {
        get => _host;
        set { _host = value ?? ""; RaisePropertyChanged(); }
    }

    private string _token = "";
    public string Token
    {
        get => _token;
        set { _token = value ?? ""; RaisePropertyChanged(); }
    }

    private string _statusText = "准备就绪";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; RaisePropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsNotBusy));
            DownloadAndIngestCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotBusy => !_isBusy;

    public RelayCommand DownloadAndIngestCommand { get; }

    public NodelistViewModel(
        NodelistDownloader downloader,
        NodelistIngestor ingestor,
        NodelistRepository repo)
    {
        _downloader = downloader;
        _ingestor = ingestor;
        _repo = repo;

        DownloadAndIngestCommand = new RelayCommand(
            async _ => await DownloadAndIngestAsync(),
            _ => IsNotBusy && !string.IsNullOrWhiteSpace(NodelistDirectory));
    }

    public sealed class EntryRow
    {
        public string Author { get; set; } = "";
        public string RepoName { get; set; } = "";
        public string FirstSeenAt { get; set; } = "";
        public string LastIngestedAt { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public sealed class DetailRow
    {
        public string Version { get; set; } = "";
        public string Stars { get; set; } = "";
        public string License { get; set; } = "";
    }

    public async Task ReloadAsync()
    {
        var entries = _repo.GetAllEntries();
        Entries.Clear();
        Details.Clear();
        foreach (var e in entries)
        {
            Entries.Add(new EntryRow
            {
                Author = e.Author,
                RepoName = e.RepoName,
                FirstSeenAt = e.FirstSeenAt,
                LastIngestedAt = e.LastIngestedAt ?? "",
                Source = e.Source,
            });
            // 加载 detail 一对多
            foreach (var d in _repo.GetDetailsByAuthor(e.Author, e.RepoName))
            {
                Details.Add(new DetailRow
                {
                    Version = d.Version,
                    Stars = d.Stars?.ToString() ?? "",
                    License = d.License ?? "",
                });
            }
        }
    }

    private async Task DownloadAndIngestAsync()
    {
        if (string.IsNullOrWhiteSpace(NodelistDirectory)) return;
        IsBusy = true;
        try
        {
            // 1. 下载
            StatusText = "下载 default custom-node-list.json...";
            var dl = await _downloader.DownloadDefaultAsync(NodelistDirectory);
            StatusText = $"下载完成 ({dl.SizeBytes / 1024} KB) → {dl.FilePath}";

            // 2. 入库
            StatusText = "解析 + 入库中...";
            var progress = new Progress<NodelistIngestor.IngestProgress>(p =>
                StatusText = $"入库中({p.Current}/{p.Total}):{p.Author}/{p.RepoName}");
            var result = await _ingestor.IngestAsync(
                dl.FilePath, Host, Token, forceFull: false, progress: progress);
            StatusText = $"入库完成 — 扫 {result.EntriesScanned},新增 {result.EntriesNew},跳过 {result.EntriesSkipped},详情 {result.DetailsWritten}(失败 {result.DetailsFailed})";

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
