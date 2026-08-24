using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>v1.0.0:本地模型视图模型。扫 <c>Settings.DefaultModelsDirectory</c>,
/// 按 <c>SourceId</c> 合并多版本,顶部 kind chip filter + 2 列卡片。View-only。</summary>
public sealed class LocalModelsViewModel : INotifyPropertyChanged
{
    private readonly Settings _settings;
    private readonly ModelFilesystemScanner _scanner;
    private readonly AppLogger? _logger;
    private readonly RelayCommand _reloadCommand;
    private List<LocalModelCard> _allCards = new();

    public ObservableCollection<LocalModelCard> FilteredModels { get; } = new();
    public ObservableCollection<KindChip> KindChips { get; } = new();
    public string? EmptyMessage { get; private set; }
    public bool IsBusy { get; private set; }

    public ICommand ReloadCommand => _reloadCommand;

    public event PropertyChangedEventHandler? PropertyChanged;

    private KindChip? _activeChip;
    public KindChip? ActiveChip
    {
        get => _activeChip;
        set
        {
            _activeChip = value;
            PropertyChanged?.Invoke(this, new(nameof(ActiveChip)));
            ApplyFilter();
        }
    }

    public LocalModelsViewModel(Settings settings, ModelFilesystemScanner scanner, AppLogger? logger = null)
    {
        _settings = settings;
        _scanner = scanner;
        _logger = logger;
        _reloadCommand = new RelayCommand(_ => ReloadAsync(), _ => !IsBusy);
    }

    public void Initialize() => ReloadAsync();

    public async Task ReloadAsync()
    {
        IsBusy = true;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        _reloadCommand.RaiseCanExecuteChanged();

        var dir = _settings.DefaultModelsDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            EmptyMessage = "未配置 Models 目录 — 请在设置中配置";
            _allCards = new();
        }
        else
        {
            IReadOnlyList<DownloadedModel> raw;
            try
            {
                raw = await Task.Run(() => _scanner.Scan(dir)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Warn("local-models", $"scan failed: {ex.Message}");
                raw = Array.Empty<DownloadedModel>();
            }
            _allCards = GroupToCards(raw);
            EmptyMessage = _allCards.Count == 0 ? "暂无已下载模型" : null;
        }

        RebuildKindChips();
        ActiveChip = KindChips[0];   // "全部" chip
        ApplyFilter();

        IsBusy = false;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyMessage)));
        _reloadCommand.RaiseCanExecuteChanged();
    }

    private static List<LocalModelCard> GroupToCards(IReadOnlyList<DownloadedModel> raw)
    {
        return raw
            .GroupBy(d => d.SourceId)
            .Select(g =>
            {
                var first = g.First();
                var latest = g.Max(d => d.DownloadedAt);
                return new LocalModelCard(
                    Title: first.Title ?? "",
                    Kind: first.Kind,
                    Source: first.Source,
                    VersionCount: g.Count(),
                    LatestDownloadedAt: latest,
                    SourceUrl: null);
            })
            .OrderByDescending(c => c.LatestDownloadedAt ?? DateTime.MinValue)
            .ToList();
    }

    private void RebuildKindChips()
    {
        KindChips.Clear();
        KindChips.Add(new KindChip(null, "全部", _allCards.Count));
        var byKind = _allCards.GroupBy(c => c.Kind).OrderBy(g => g.Key.ToString());
        foreach (var g in byKind)
        {
            KindChips.Add(new KindChip(g.Key, g.Key.ToString(), g.Count()));
        }
    }

    private void ApplyFilter()
    {
        FilteredModels.Clear();
        var src = _activeChip?.Kind is null
            ? _allCards
            : _allCards.Where(c => c.Kind == _activeChip!.Kind).ToList();
        foreach (var c in src) FilteredModels.Add(c);
    }
}

public sealed record LocalModelCard(
    string Title,
    ModelKind Kind,
    string Source,
    int VersionCount,
    DateTime? LatestDownloadedAt,
    string? SourceUrl);

public sealed record KindChip(ModelKind? Kind, string Display, int Count);
