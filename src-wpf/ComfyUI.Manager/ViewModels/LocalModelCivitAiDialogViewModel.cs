using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0 T11:CivitAI lookup dialog VM — 4 状态机(Searching / NoMatch / Picker / Detail)。
/// 调用方(<see cref="LocalModelsViewModel"/>)在用户点 Local 卡片的 [查询 CivitAI] 按钮后
/// new 一个本 VM + 新 Window,modal 弹给用户选 / 看。Service 注入而非自己构造 ——
/// T9a CivitAiLookupService 是真服务,T11 是消费者.
///
/// v1.0.0 T13-7:加可选 <c>card</c> ctor 参数 — Scanner 在 Reload 时已经 hash-match 过的话
/// (4 策略链),card.MatchedDetail 非 null → dialog 直接开 Detail state,跳过 Searching。
/// 用户对已 matched 的本地卡单击按钮直接看详情(不再打字搜),loading 阶段从 scan 移到按钮 click。
/// card = null (默认) 时维持旧 behavior — LoadAsync 走 search-by-title 路径.
///
/// 状态流:
///   LoadAsync(searchTitle)
///     - 0 candidate → NoMatch
///     - 1 candidate → auto GetDetailAsync → Detail
///     - 2+ candidate → Picker,等用户 SelectCandidate → Detail
///
/// 错误处理:任何网络 / JSON 错误 → State = NoMatch,YAGNI 不加 Error 状态
/// (跟 EditTemplateDialogViewModel "错误信息 inline 显示" 一致 — 用户不需要细化错误码)。
/// </summary>
public sealed class LocalModelCivitAiDialogViewModel : INotifyPropertyChanged
{
    private readonly CivitAiLookupService _lookup;
    private readonly AppLogger? _logger;
    private readonly string _searchTitle;
    private readonly LocalModelCard? _card;

    public LocalModelCivitAiDialogViewModel(
        CivitAiLookupService lookup,
        string searchTitle,
        AppLogger? logger = null,
        LocalModelCard? card = null)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _searchTitle = searchTitle ?? "";
        _logger = logger;
        _card = card;

        // v1.0.0 T13-7:Pre-matched card (Scanner 在 ReloadAsync 时已经命中 hash-match chain)
        // → dialog 直接开 Detail state,跳过 Searching 阶段。MatchedDetail 在 scanner 阶段
        // 已经从 CivitAI 拉过详情,不需要再查一遍。
        if (card?.MatchedDetail is not null)
        {
            State = DialogState.Detail;
            Detail = card.MatchedDetail;
            SelectedCandidate = null;
            Candidates = Array.Empty<CivitAiCandidate>();
        }
    }

    public string Title => _searchTitle;
    public DialogState State { get; private set; } = DialogState.Searching;
    public IReadOnlyList<CivitAiCandidate> Candidates { get; private set; }
        = Array.Empty<CivitAiCandidate>();
    public CivitAiCandidate? SelectedCandidate { get; set; }
    public CivitAiDetailDto? Detail { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发一次搜索。Initial state 是 Searching;成功后根据 candidate count 切到
    /// NoMatch / Detail / Picker。失败 → NoMatch。Modal dialog 的 Loaded event 后调。
    /// ConfigureAwait(true) 让 await 续段回到 UI SynchronizationContext(WPF ObservableCollection
    /// 跨线程写会抛异常 — 同 v0.6.19.x workflow marketplace lesson)。
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // v1.0.0 T13-7:Pre-matched card → ctor 已开 Detail state,LoadAsync 不再 search
        // (MatchedDetail 已在 scanner 阶段 fetch 过,再搜是浪费 + 浪费 token)。
        if (_card?.MatchedDetail is not null)
        {
            return;
        }

        State = DialogState.Searching;
        Raise(nameof(State));

        try
        {
            var candidates = await _lookup.SearchByTitleAsync(_searchTitle, ct)
                .ConfigureAwait(true);
            if (candidates.Count == 0)
            {
                State = DialogState.NoMatch;
                Candidates = Array.Empty<CivitAiCandidate>();
                Detail = null;
            }
            else if (candidates.Count == 1)
            {
                SelectedCandidate = candidates[0];
                Candidates = candidates;   // 单候选也存 — "返回候选" 按钮 InverseZeroCount 守卫 (Picker 状态可用,但 1 个也无所谓)
                await LoadDetailAsync(candidates[0].Id, ct).ConfigureAwait(true);
            }
            else
            {
                State = DialogState.Picker;
                Candidates = candidates;
                Detail = null;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户关窗 / token 取消 — 维持 Searching 但 dialog 已经关,没后续 UI 操作
            _logger?.Info("civitai-lookup-dialog", "搜索已取消");
        }
        catch (Exception ex)
        {
            _logger?.Error("civitai-lookup-dialog",
                $"✗ {ex.GetType().Name}: {ex.Message}");
            State = DialogState.NoMatch;
            Candidates = Array.Empty<CivitAiCandidate>();
            Detail = null;
        }
        finally
        {
            Raise(nameof(State), nameof(Candidates), nameof(SelectedCandidate), nameof(Detail));
        }
    }

    /// <summary>Picker 选中 1 个 candidate → fetch 详情 + 切 Detail state。</summary>
    public async Task SelectCandidateAsync(CivitAiCandidate c, CancellationToken ct = default)
    {
        // v1.0.0 T13-7:Pre-matched card → 状态已在 Detail,忽略 SelectCandidate 调用
        // (CardFromCardFlow 不应该发生,但防 caller 错误调一次也无害)
        if (_card?.MatchedDetail is not null) return;

        SelectedCandidate = c;
        Raise(nameof(SelectedCandidate));
        await LoadDetailAsync(c.Id, ct).ConfigureAwait(true);
    }

    /// <summary>Detail → 返回 Picker。必须 ≥2 candidates 才生效。</summary>
    public void BackToPicker()
    {
        if (Candidates.Count <= 1) return;
        State = DialogState.Picker;
        Detail = null;
        Raise(nameof(State), nameof(Detail));
    }

    private async Task LoadDetailAsync(int id, CancellationToken ct)
    {
        try
        {
            Detail = await _lookup.GetDetailAsync(id, ct).ConfigureAwait(true);
            State = DialogState.Detail;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CivitAiLookupNotFoundException)
        {
            // 详情 404 — 跳回 NoMatch(用户拿不到这模型)
            _logger?.Warn("civitai-lookup-dialog", $"详情 {id} 404 not found");
            State = DialogState.NoMatch;
            Detail = null;
        }
        catch (Exception ex)
        {
            _logger?.Error("civitai-lookup-dialog",
                $"detail ✗ {ex.GetType().Name}: {ex.Message}");
            State = DialogState.NoMatch;
            Detail = null;
        }
        finally
        {
            Raise(nameof(State), nameof(Detail));
        }
    }

    private void Raise(params string[] names)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        foreach (var n in names)
        {
            handler.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}

/// <summary>v1.0.0 T11:dialog 4 状态机。XAML 用 EnumEqualsVisibility 切 4 个 Grid/StackPanel。</summary>
public enum DialogState
{
    Searching,
    NoMatch,
    Picker,
    Detail,
}
