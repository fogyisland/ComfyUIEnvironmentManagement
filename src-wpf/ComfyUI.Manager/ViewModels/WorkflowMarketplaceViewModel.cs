using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>v0.6.19:工作流市场 VM — 镜像 EnvironmentListView inline 模式
/// (filter/sort/multi-select/console/refresh/batch-download)。
/// Console 三态可见性跟 v0.6.18.4 BulkUpdateViewModel 同款:!userHidden && (IsBusy || hasContent)。</summary>
public class WorkflowMarketplaceViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly WorkflowMarketplaceService _marketplace;
    private readonly WorkflowDownloader _downloader;
    private readonly WorkflowFilesystemScanner _scanner;
    private readonly AppLogger? _logger;
    private readonly List<WorkflowEntry> _allEntries = new();
    private bool _userHiddenConsole;

    private string _searchText = "";
    private WorkflowSortKind _sortBy = WorkflowSortKind.Newest;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _infoMessage;

    public WorkflowMarketplaceViewModel(
        Settings settings, WorkflowMarketplaceService marketplace,
        WorkflowDownloader downloader, WorkflowFilesystemScanner scanner,
        AppLogger? logger = null)
    {
        _settings = settings;
        _marketplace = marketplace;
        _downloader = downloader;
        _scanner = scanner;
        _logger = logger;

        Workflows = new ObservableCollection<WorkflowEntry>();
        ActiveSourceFilters = new ObservableCollection<WorkflowSourceKind>();
        Selected = new ObservableCollection<WorkflowEntry>();
        ConsoleLog = new ObservableCollection<string>();

        // 默认全选 3 个 source
        foreach (var s in new[] { WorkflowSourceKind.CommunityJson, WorkflowSourceKind.CivitAi, WorkflowSourceKind.OpenArt })
        {
            ActiveSourceFilters.Add(s);
        }

        Selected.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(SelectedCount));
            BatchDownloadCommand.RaiseCanExecuteChanged();
        };

        ActiveSourceFilters.CollectionChanged += (_, _) => ApplyFilter();
        ConsoleLog.CollectionChanged += OnConsoleLogChanged;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => Workflows.Count > 0);
        BatchDownloadCommand = new RelayCommand(async _ => await BatchDownloadAsync(),
            _ => HasSelection && !IsBusy && ResolveWorkflowsDirOk());
        ClearConsoleCommand = new RelayCommand(_ => ClearConsole());
        OpenFolderCommand = new RelayCommand(_ => OpenWorkflowsFolder(), _ => ResolveWorkflowsDirOk());
        DownloadSingleCommand = new RelayCommand(async p => await DownloadSingleAsync(p as WorkflowEntry),
            p => p is WorkflowEntry && !IsBusy && ResolveWorkflowsDirOk());
        ClearSearchCommand = new RelayCommand(_ => SearchText = "", _ => HasSearchText);
    }

    // —— Outputs ——
    public ObservableCollection<WorkflowEntry> Workflows { get; }
    public ObservableCollection<WorkflowSourceKind> ActiveSourceFilters { get; }
    public ObservableCollection<WorkflowEntry> Selected { get; }
    public ObservableCollection<string> ConsoleLog { get; }
    public int SelectedCount => Selected.Count;
    public bool HasSelection => Selected.Count > 0;

    /// <summary>v0.6.22: search input is non-empty — drives ✕ clear button Visibility
    /// via BoolToVisibility converter in WorkflowMarketplaceView.xaml Row 0.</summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            ApplyFilter();
            RaisePropertyChanged(nameof(HasSearchText));
        }
    }
    public WorkflowSortKind SortBy
    {
        get => _sortBy;
        set { if (_sortBy == value) return; _sortBy = value; ApplyFilter(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsConsoleVisible));
            RaisePropertyChanged(nameof(DownloadsEnabled));
            RaisePropertyChanged(nameof(NotIsBusy));
            RaisePropertyChanged(nameof(IsEmpty));
            RefreshCommand.RaiseCanExecuteChanged();
            BatchDownloadCommand.RaiseCanExecuteChanged();
        }
    }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(IsEmpty)); }
    }
    public string? InfoMessage
    {
        get => _infoMessage;
        private set { _infoMessage = value; RaisePropertyChanged(); }
    }
    public int TotalCount => _allEntries.Count;
    public int DownloadedCount { get; private set; }
    public bool DownloadsEnabled => ResolveWorkflowsDirOk();

    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    // v0.6.19.x UI polish:loading overlay / button-disable 用 NotIsBusy 比 DataTrigger 简洁。
    public bool NotIsBusy => !IsBusy;

    // v0.6.19.x UI polish:空状态文案条件 — 不是忙 + 没结果 + 没错误信息。
    // ErrorMessage 非空时优先显示错误条,不显示空状态。
    public bool IsEmpty => !IsBusy && Workflows.Count == 0 && ErrorMessage is null;

    // —— Commands ——
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }
    public RelayCommand BatchDownloadCommand { get; }
    public RelayCommand ClearConsoleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand DownloadSingleCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Initial fetch + scan。call after view constructed。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        ScanDownloaded();
        await RefreshAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var results = await _marketplace.LoadAllAsync(SearchText, maxResultsPerSource: 50, ct);
            _allEntries.Clear();
            _allEntries.AddRange(results);
            ApplyFilter();
            ScanDownloaded();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"刷新失败:{ex.Message}";
            _logger?.Error("workflow-marketplace", "refresh failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allEntries.AsEnumerable();

        // text
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var q = _searchText.ToLowerInvariant();
            filtered = filtered.Where(e =>
                (e.Title?.ToLowerInvariant().Contains(q) ?? false) ||
                (e.Author?.ToLowerInvariant().Contains(q) ?? false) ||
                e.Tags.Any(t => t.ToLowerInvariant().Contains(q)));
        }

        // source
        if (ActiveSourceFilters.Count > 0)
        {
            filtered = filtered.Where(e => ActiveSourceFilters.Contains(e.Source));
        }

        // sort
        filtered = _sortBy switch
        {
            WorkflowSortKind.Downloads => filtered.OrderByDescending(e => e.DownloadCount ?? 0),
            WorkflowSortKind.Name => filtered.OrderBy(e => e.Title),
            _ => filtered.OrderByDescending(e => e.PublishedAt ?? DateTimeOffset.MinValue),
        };

        var list = filtered.ToList();
        Workflows.Clear();
        foreach (var e in list) Workflows.Add(e);

        RaisePropertyChanged(nameof(TotalCount));
        RaisePropertyChanged(nameof(IsEmpty));
    }

    private void ToggleSelectAll()
    {
        if (Selected.Count == Workflows.Count)
        {
            Selected.Clear();
        }
        else
        {
            foreach (var e in Workflows)
            {
                if (!Selected.Contains(e)) Selected.Add(e);
            }
        }
    }

    private async Task BatchDownloadAsync()
    {
        if (Selected.Count == 0 || IsBusy) return;
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir)) { ErrorMessage = "工作流目录未配置"; return; }
        IsBusy = true;
        ConsoleLog.Clear();
        _userHiddenConsole = false;
        try
        {
            var entries = Selected.ToList();
            var log = new Progress<string>(line => ConsoleLog.Add(line));
            var summary = await _downloader.DownloadBatchAsync(entries, dir, log);
            InfoMessage = $"批量下载完成:成功 {summary.Succeeded} / 失败 {summary.Failed}";
            ScanDownloaded();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"批量下载失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadSingleAsync(WorkflowEntry? entry)
    {
        if (entry is null || IsBusy) return;
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir)) { ErrorMessage = "工作流目录未配置"; return; }
        IsBusy = true;
        try
        {
            var log = new Progress<string>(line => ConsoleLog.Add(line));
            var result = await _downloader.DownloadAsync(entry, dir, log);
            if (result.Success) InfoMessage = $"已下载:{entry.Title}";
            else ErrorMessage = $"下载失败:{result.FailureReason}";
            ScanDownloaded();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ScanDownloaded()
    {
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            DownloadedCount = 0;
        }
        else
        {
            DownloadedCount = _scanner.Scan(dir).Count;
        }
        RaisePropertyChanged(nameof(DownloadedCount));
    }

    public void ClearConsole()
    {
        ConsoleLog.Clear();
        _userHiddenConsole = true;
        RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    // v0.6.19.x UI polish:错误 / 信息 banner ✕ 按钮调用。
    public void ClearErrorMessage() => ErrorMessage = null;
    public void ClearInfoMessage() => InfoMessage = null;

    private void OpenWorkflowsFolder()
    {
        var dir = ResolveWorkflowsDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems is { Count: > 0 })
            RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    private bool ResolveWorkflowsDirOk()
    {
        var dir = ResolveWorkflowsDir();
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
    }

    private string? ResolveWorkflowsDir()
    {
        var dir = _settings.WorkflowsDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        if (!Path.IsPathRooted(dir))
        {
            var root = Path.GetDirectoryName(System.Environment.ProcessPath);
            if (!string.IsNullOrEmpty(root)) dir = Path.Combine(root, dir);
        }
        return dir;
    }
}

public enum WorkflowSortKind { Newest, Downloads, Name }