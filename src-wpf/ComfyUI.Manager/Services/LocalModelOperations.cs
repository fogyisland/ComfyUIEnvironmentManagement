using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Civitai;
using Microsoft.VisualBasic.FileIO;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:LocalModelCard 上下文菜单 7 命令实现层。
/// 所有公共方法 try/catch → toast + log,不抛异常(避免 ContextMenu 残留)。
/// 3 个测试 seam 属性允许测试 override OS API(clipboard / explorer / messagebox)。
///
/// 偏离 brief 的关键点(已 documented in task-3-report.md pre-flight):
/// 1. <c>ModelHasher</c> 是 STATIC 类(<c>e4ae8848</c> ship),不能在 ctor 当 instance 形参;
///    改为 <see cref="Func{T1, T2, TResult}"/> delegate 注入 (brief Task 3 提示用 NSubstitute
///    substitute 但 static class 不可 substitute — delegate 注入更直接)。
/// 2. <c>CivitaiMatcherOrchestrator.MatchByHashAsync(string, ct)</c> 不存在;只有
///    <c>MatchAsync(DownloadedModel, ct): Task<MatchResult?></c>;改为 delegate 注入。
///    生产 wire 在 MainViewModel 注入 <c>orchestrator.MatchAsync</c> method group。
/// 3. <c>LocalModelCard.SourcePath</c> 已由 T3 task scope 加入 record(brief decision (a))。
///
/// VM 层(T4 task)用 7 RelayCommand 包装这些方法;DB 写 + VM Refresh 留 TODO(T5/T6 wire)。
/// </summary>
public sealed class LocalModelOperations
{
    private readonly ToastNotification _toast;
    private readonly StarredModelsRepository _stars;
    /// <summary>hash 函数 delegate —— 生产 wire 用 <c>ModelHasher.ComputeTensorOnlySha256</c>;
    /// 测试直接传 fixture-friendly lambda。</summary>
    private readonly Func<string, CancellationToken, string> _hashFunc;
    /// <summary>CivitAI metadata 查找 delegate —— 生产 wire 用 <c>orchestrator.MatchAsync</c>;
    /// 返回 null = 未在 CivitAI 找到匹配。</summary>
    private readonly Func<DownloadedModel, CancellationToken, Task<MatchResult?>> _matchFunc;
    private readonly IProgressDialogService _progress;

    // ===== 测试 seam (默认走真实 OS API,测试可 override) =====

    /// <summary>v1.0.0.x T47:删除确认对话框 seam —— 默认走 <see cref="MessageBox.Show"/>。
    /// 测试 override 为 lambda 返回 <c>Yes</c>/<c>No</c> 跳过真实弹窗。</summary>
    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowConfirmDialogOverride { get; set; }
        = (msg, title, btn, icon) => MessageBox.Show(msg, title, btn, icon);

    /// <summary>v1.0.0.x T47:explorer.exe /select seam —— 默认走 <see cref="ProcessStart.OpenInExplorer"/>。
    /// 测试 override 为 lambda 验证路径参数。</summary>
    public Action<string> OpenInExplorerOverride { get; set; }
        = path => ProcessStart.OpenInExplorer(path);

    /// <summary>v1.0.0.x T47:剪贴板写入 seam —— 默认走 <see cref="Clipboard.SetText"/>。
    /// 测试 override 为 lambda 捕获写入文本。</summary>
    public Action<string> SetClipboardTextOverride { get; set; }
        = text => Clipboard.SetText(text);

    /// <summary>v1.0.0.x T47:构造 LocalModelOperations。
    /// 生产 wire (MainViewModel / T4 task):
    /// <code>
    /// new LocalModelOperations(
    ///     toast, stars,
    ///     ModelHasher.ComputeTensorOnlySha256,           // static → method group
    ///     civitaiOrchestrator.MatchAsync,                 // instance → method group
    ///     progress)
    /// </code>
    /// </summary>
    public LocalModelOperations(
        ToastNotification toast,
        StarredModelsRepository stars,
        Func<string, CancellationToken, string> hashFunc,
        Func<DownloadedModel, CancellationToken, Task<MatchResult?>> matchFunc,
        IProgressDialogService progress)
    {
        _toast = toast ?? throw new ArgumentNullException(nameof(toast));
        _stars = stars ?? throw new ArgumentNullException(nameof(stars));
        _hashFunc = hashFunc ?? throw new ArgumentNullException(nameof(hashFunc));
        _matchFunc = matchFunc ?? throw new ArgumentNullException(nameof(matchFunc));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    // ===================== Star 收藏 =====================

    /// <summary>v1.0.0.x T47:星标/取消星标。<c>card.SourcePath</c> 作为 DB PK;
    /// 同一路径二次 Star 自动去重(<c>INSERT OR IGNORE</c>)。
    /// 异常吞 → toast.Error,不影响 ContextMenu 关闭。
    /// 返回原 card(LocalModelCard 暂无 IsStarred 派生字段,T4 加;此方法仅 toggle DB 状态)。</summary>
    public LocalModelCard ToggleStar(LocalModelCard card)
    {
        try
        {
            if (string.IsNullOrEmpty(card.SourcePath))
            {
                _toast.Warning("LocalModel: 路径为空,无法收藏");
                return card;
            }

            if (_stars.IsStarred(card.SourcePath))
            {
                _stars.Remove(card.SourcePath);
                _toast.Success("LocalModel: ⭐ 已取消收藏");
            }
            else
            {
                _stars.Add(card.SourcePath);
                _toast.Success("LocalModel: ⭐ 已收藏");
            }
            return card;
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: 收藏失败 - {ex.Message}");
            return card;
        }
    }

    // ===================== 复制 Hash =====================

    /// <summary>v1.0.0.x T47:复制 SHA256 到剪贴板。Hash 为 null 时 toast 警告 + 早返回。
    /// 剪贴板 COM 异常被 catch → toast.Error。</summary>
    public void CopyHash(LocalModelCard card)
    {
        try
        {
            if (string.IsNullOrEmpty(card.Hash))
            {
                _toast.Warning("LocalModel: 无 Hash 可复制");
                return;
            }
            SetClipboardTextOverride(card.Hash);
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: 剪贴板不可用 - {ex.Message}");
        }
    }

    // ===================== 打开文件夹 =====================

    /// <summary>v1.0.0.x T47:explorer.exe /select 打开文件所在文件夹。
    /// 路径不存在 → toast.Warning + 早返回;explorer 异常 → toast.Error。</summary>
    public void OpenFolder(LocalModelCard card)
    {
        try
        {
            if (string.IsNullOrEmpty(card.SourcePath) || !File.Exists(card.SourcePath))
            {
                _toast.Warning($"LocalModel: 路径不存在 - {card.SourcePath}");
                return;
            }
            OpenInExplorerOverride(card.SourcePath);
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: 无法打开文件夹 - {ex.Message}");
        }
    }

    // ===================== 重新计算 Hash =====================

    /// <summary>v1.0.0.x T47:进度对话框 + 重算 SHA256(tensor-only fast path)。
    /// DB 写 + VM Refresh 留 TODO(T5/T6 wire);本方法仅算 hash + toast 反馈。
    /// 任何异常(FileNotFound / IO / hashFunc 抛)→ toast.Error。</summary>
    public async Task ReloadHashAsync(LocalModelCard card, CancellationToken ct)
    {
        try
        {
            using var _ = _progress.ShowIndeterminate($"LocalModel: 重新计算 Hash - {Path.GetFileName(card.SourcePath)}");
            var hash = await Task.Run(() => _hashFunc(card.SourcePath, ct), ct).ConfigureAwait(false);
            // TODO Task 5/6 wire:写 model.db local_model_files.sha256 + 通知 VM Refresh
            _toast.Success($"LocalModel: Hash 已更新 - {Path.GetFileName(card.SourcePath)} ({hash})");
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: Hash 计算失败 - {ex.Message}");
        }
    }

    // ===================== 从 CivitAI 刷新元数据 =====================

    /// <summary>v1.0.0.x T47:进度对话框 + 走 CivitAI 链查找 metadata。
    /// 查找返回 null → toast.Info(未匹配);返回 detail → toast.Success。
    /// 任何异常(API 失败 / 网络)→ toast.Error。
    /// DB 写 + VM Refresh 留 TODO(T5/T6 wire)。</summary>
    public async Task RefreshMetadataAsync(LocalModelCard card, CancellationToken ct)
    {
        try
        {
            using var _ = _progress.ShowIndeterminate($"LocalModel: 从 CivitAI 刷新 - {Path.GetFileName(card.SourcePath)}");

            // 构造 DownloadedModel 用 card.SourcePath + card.Hash 给 orchestrator chain
            var probe = new DownloadedModel
            {
                FullPath = card.SourcePath,
                Hash = card.Hash,
                Kind = card.Kind,
                Source = card.Source,
                SourceId = card.SourceId,
                Title = card.Title,
            };
            var matchResult = await _matchFunc(probe, ct).ConfigureAwait(false);
            if (matchResult == null)
            {
                _toast.Info("LocalModel: 未在 CivitAI 找到匹配");
                return;
            }
            // TODO Task 5/6 wire:写 model.db matched_model_details + 通知 VM Refresh
            _toast.Success($"LocalModel: CivitAI 元数据已刷新 - {matchResult.Detail.Title}");
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: CivitAI 失败 - {ex.Message}");
        }
    }

    // ===================== 复制 SourceUrl =====================

    /// <summary>v1.0.0.x T47:复制 <c>https://civitai.com/models/{Id}</c> 到剪贴板。
    /// MatchedDetail 为 null → 早返回(无 URL 可拼)。XAML 端 IsEnabled 也会挡。</summary>
    public void CopySourceUrl(LocalModelCard card)
    {
        try
        {
            if (card.MatchedDetail?.Id is not int id) return;
            var url = $"https://civitai.com/models/{id}";
            SetClipboardTextOverride(url);
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: 复制 URL 失败 - {ex.Message}");
        }
    }

    // ===================== 复制文件名 =====================

    /// <summary>v1.0.0.x T47:<c>Path.GetFileName(card.SourcePath)</c> → 剪贴板。
    /// Path.GetFileName 异常(非法路径)→ toast.Warning。</summary>
    public void CopyFileName(LocalModelCard card)
    {
        try
        {
            var filename = Path.GetFileName(card.SourcePath);
            if (string.IsNullOrEmpty(filename))
            {
                _toast.Warning("LocalModel: 文件名解析为空");
                return;
            }
            SetClipboardTextOverride(filename);
        }
        catch (Exception ex)
        {
            _toast.Warning($"LocalModel: 文件名解析失败 - {ex.Message}");
        }
    }

    // ===================== 删除到回收站 =====================

    /// <summary>v1.0.0.x T47:MessageBox 确认 → Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile
    /// (Recycle Bin,可恢复)→ toast.Success。文件已不存在 → info toast + 走 DB 清理分支。
    /// DB 写 + VM Refresh 留 TODO(T5/T6 wire)。
    /// 任何异常(占用 / 权限 / 文件被锁)→ toast.Error。</summary>
    public void Delete(LocalModelCard card)
    {
        try
        {
            if (string.IsNullOrEmpty(card.SourcePath))
            {
                _toast.Warning("LocalModel: 路径为空,无法删除");
                return;
            }
            var filename = Path.GetFileName(card.SourcePath);
            var confirm = ShowConfirmDialogOverride(
                $"确定删除 {filename}?\n文件会进入回收站,可恢复。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            if (!File.Exists(card.SourcePath))
            {
                _toast.Info($"LocalModel: 文件已不存在,仅清理数据库 - {filename}");
                // TODO Task 5/6 wire:删 model.db entry + 通知 VM Refresh
                return;
            }

            FileSystem.DeleteFile(card.SourcePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            // TODO Task 5/6 wire:删 model.db entry + 通知 VM Refresh
            _toast.Success($"LocalModel: 已删除 - {filename}");
        }
        catch (Exception ex)
        {
            _toast.Error($"LocalModel: 删除失败 - {ex.Message}");
        }
    }

    // ===================== Private helpers =====================

    /// <summary>v1.0.0.x T47:explorer.exe /select 默认实现 ——
    /// 启动 explorer 并选中文件。Process 生命周期短(launcher 立即返回 explorer 句柄)。
    /// 任何启动异常向上抛(由 OpenFolder catch)。</summary>
    private static class ProcessStart
    {
        public static void OpenInExplorer(string path)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }
}
