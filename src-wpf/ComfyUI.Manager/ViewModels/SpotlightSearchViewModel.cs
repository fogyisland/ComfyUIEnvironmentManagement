using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Search;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.9 T7:Spotlight popup 的 VM。
/// <para>
/// 状态机:
///   - <see cref="IsOpen"/> = true 时显示 popup + Query 框 focus
///   - <see cref="IsBuilding"/> = 第一次 BuildAsync 进行中(显示"加载中...")
///   - <see cref="IsUnavailable"/> = BuildAsync 抛 exception(显示"⚠ 搜索不可用")
/// </para>
/// <para>
/// G7:键入仅走内存,BuildAsync 只跑一次(<see cref="_index"/> ??= cache)。
/// 第二次起 OpenAsync 立即返回 + UpdateResults 直接同步用索引。
/// </para>
/// <para>
/// Navigator 通过 <c>Func&lt;SearchTarget, Task&gt;</c> 注入(不是直接持 MainVM 引用),
/// 避免 VM cycle + 让 test seam 简单。
/// </para>
/// </summary>
public sealed class SpotlightSearchViewModel : ViewModelBase
{
    private readonly IGlobalSearchService _service;
    private readonly Func<SearchTarget, Task> _navigator;
    private SearchIndex? _index;

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? ""))
            {
                if (IsOpen) UpdateResults();
            }
        }
    }

    private IReadOnlyList<SearchResult> _results = Array.Empty<SearchResult>();
    public IReadOnlyList<SearchResult> Results
    {
        get => _results;
        private set
        {
            if (SetField(ref _results, value))
            {
                RaisePropertyChanged(nameof(CanGoUp));
                RaisePropertyChanged(nameof(CanGoDown));
                UpCommand.RaiseCanExecuteChanged();
                DownCommand.RaiseCanExecuteChanged();
                EnterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => SetField(ref _selectedIndex, value);
    }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    private bool _isBuilding;
    public bool IsBuilding
    {
        get => _isBuilding;
        private set => SetField(ref _isBuilding, value);
    }

    private bool _isUnavailable;
    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set => SetField(ref _isUnavailable, value);
    }

    public bool CanGoUp => Results.Count > 0;
    public bool CanGoDown => Results.Count > 0;

    public RelayCommand OpenCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    public RelayCommand EnterCommand { get; }

    public SpotlightSearchViewModel(
        IGlobalSearchService service,
        Func<SearchTarget, Task> navigator)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        OpenCommand = new RelayCommand(_ => _ = OpenAsync());
        CloseCommand = new RelayCommand(_ => Close());
        UpCommand = new RelayCommand(_ => MoveSelection(-1), _ => CanGoUp);
        DownCommand = new RelayCommand(_ => MoveSelection(+1), _ => CanGoDown);
        EnterCommand = new RelayCommand(
            async _ => await ExecuteSelectedAsync(),
            _ => Results.Count > 0);
    }

    /// <summary>
    /// 打开 popup。第一次 BuildAsync 跑异步(<see cref="IsBuilding"/> 转 true 期间
    /// 显示加载提示),后续立刻返回。BuildAsync 抛异常 → <see cref="IsUnavailable"/> = true。
    /// </summary>
    public async Task OpenAsync()
    {
        IsOpen = true;
        Query = "";
        Results = Array.Empty<SearchResult>();
        SelectedIndex = 0;
        await EnsureIndexAsync();
    }

    public void Close() => IsOpen = false;

    private async Task EnsureIndexAsync()
    {
        if (_index is not null) return;
        IsBuilding = true;
        try
        {
            _index = await _service.BuildAsync();
        }
        catch
        {
            // BuildAsync 失败 → 不可用态。Index 留 null,Results 留空。
            IsUnavailable = true;
        }
        finally
        {
            IsBuilding = false;
        }
    }

    private void UpdateResults()
    {
        if (_index is null)
        {
            Results = Array.Empty<SearchResult>();
            return;
        }
        Results = _index.Query(Query);
        if (SelectedIndex >= Results.Count) SelectedIndex = Results.Count - 1;
        if (SelectedIndex < 0) SelectedIndex = 0;
    }

    private void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        var i = SelectedIndex + delta;
        if (i < 0) i = Results.Count - 1;
        else if (i >= Results.Count) i = 0;
        SelectedIndex = i;
    }

    private async Task ExecuteSelectedAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var target = Results[SelectedIndex].Entry.Target;
        Close();
        await _navigator(target);
    }
}