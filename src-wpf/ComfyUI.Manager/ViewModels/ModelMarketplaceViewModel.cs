using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.20 T8:模型市场 ViewModel。镜像 v0.6.19 WorkflowMarketplaceViewModel 模式:
/// toolbar / kind filter chips / card grid / console panel + 3-state console 可见性。
/// 卡片内容 = ModelEntry (title + Kind badge + NsfwKind badge + per-version CheckBox)。
/// 选中以版本为单位(不是以模型为单位)—— 1 个 CheckBox = 1 个下载任务。
/// </summary>
public class ModelMarketplaceViewModel : INotifyPropertyChanged
{
    private readonly ModelMarketplaceService _marketplace;
    private readonly ModelDownloader _downloader;
    private readonly ModelFilesystemScanner _scanner;
    private readonly Settings _settings;
    private readonly AppLogger? _logger;

    private readonly List<ModelEntry> _allModels = new();
    private bool _userHiddenConsole;
    private string _query = "";
    private ModelKind? _activeKindFilter;
    private bool _isBusy;

    /// <summary>底层 fetch 后被 filter strip 处理的"全集"。</summary>
    public ObservableCollection<ModelEntry> Models { get; } = new();

    /// <summary>已勾选要下载的版本(per-version 多选)。</summary>
    public ObservableCollection<ModelVersionEntry> SelectedVersions { get; } = new();

    /// <summary>UI 行内 Console log,同 v0.6.18.4 BulkUpdate 模式。</summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();

    /// <summary>8 个 kind 过滤选项(Unknown 排除)。</summary>
    public ObservableCollection<ModelKind> KindFilters { get; } = new(
        Enum.GetValues<ModelKind>().Where(k => k != ModelKind.Unknown));

    // —— Commands ——
    public ICommand RefreshCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand ClearConsoleLogCommand { get; }
    public ICommand HideConsoleCommand { get; }
    public ICommand ToggleVersionSelectionCommand { get; }

    public ModelMarketplaceViewModel(
        ModelMarketplaceService marketplace,
        ModelDownloader downloader,
        ModelFilesystemScanner scanner,
        Settings settings,
        AppLogger? logger)
    {
        _marketplace = marketplace;
        _downloader = downloader;
        _scanner = scanner;
        _settings = settings;
        _logger = logger;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        DownloadSelectedCommand = new RelayCommand(
            async _ => await DownloadSelectedAsync(),
            _ => SelectedVersions.Count > 0 && !IsBusy);
        ClearConsoleLogCommand = new RelayCommand(_ => ConsoleLog.Clear());
        HideConsoleCommand = new RelayCommand(_ =>
        {
            _userHiddenConsole = true;
            OnPropertyChanged(nameof(IsConsoleVisible));
        });
        ToggleVersionSelectionCommand = new RelayCommand(p =>
        {
            if (p is ModelVersionEntry v)
            {
                if (SelectedVersions.Contains(v)) SelectedVersions.Remove(v);
                else SelectedVersions.Add(v);
            }
        });

        // 3-state console visibility:任何 ConsoleLog 变化触发 IsConsoleVisible 重算
        ConsoleLog.CollectionChanged += OnConsoleLogChanged;
    }

    // —— Bindable properties ——
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public ModelKind? ActiveKindFilter
    {
        get => _activeKindFilter;
        set
        {
            if (_activeKindFilter == value) return;
            _activeKindFilter = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConsoleVisible));
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 3-state console visibility:!userHidden &amp;&amp; (IsBusy || hasContent)。
    /// 用户主动 ✕ 关闭后必须保留意图,直到下次 RefreshAsync/DownloadSelectedAsync 复位。
    /// </summary>
    public bool IsConsoleVisible => !_userHiddenConsole && (IsBusy || ConsoleLog.Count > 0);

    // —— Operations ——
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _userHiddenConsole = false;  // reset user-hidden on new refresh
        try
        {
            // VM-side await MUST NOT use .ConfigureAwait(false) — continuation runs on UI sync ctx,
            // touching Models.Clear() / Add() requires WPF-friendly context.
            var results = await _marketplace.LoadAllAsync(_query, maxResultsPerSource: 50);
            _allModels.Clear();
            _allModels.AddRange(results);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger?.Warn("model-marketplace", $"刷新失败: {ex.Message}");
            ConsoleLog.Add($"[错误] 刷新失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedVersions.Count == 0 || IsBusy) return;
        IsBusy = true;
        _userHiddenConsole = false;
        try
        {
            // Progress<string> captures the current SynchronizationContext (UI thread) at
            // construction — Report() automatically marshals back to UI thread.
            var progress = new Progress<string>(line => ConsoleLog.Add(line));
            var versions = SelectedVersions.ToList();
            var summary = await _downloader.DownloadBatchAsync(
                versions, _settings.ModelsDirectory, progress);
            ConsoleLog.Add(
                $"[完成] 成功 {summary.Succeeded}, 失败 {summary.Failed}, 耗时 {summary.TotalDuration.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            _logger?.Error("model-download", $"批量下载异常: {ex.Message}");
            ConsoleLog.Add($"[错误] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allModels.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_query))
        {
            filtered = filtered.Where(m =>
                m.Title.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }
        if (_activeKindFilter is { } k)
        {
            filtered = filtered.Where(m => m.Kind == k);
        }
        Models.Clear();
        foreach (var m in filtered) Models.Add(m);
    }

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsConsoleVisible));
    }

    // —— INotifyPropertyChanged ——
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
