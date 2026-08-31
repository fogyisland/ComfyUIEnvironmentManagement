using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0 multi-template: sidebar page VM. Lists + adds + edits + deletes templates.
/// Built-in 7 个内置模板 are protected from delete (G13):
    /// ComfyUI / Forge / OpenVoice / Whisper / CoquiTTS / Bark。
    /// v1.0.0.x: A1111 + SwarmUI 模板已下线,不再 seed。
/// </summary>
public class TemplateManagementViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly Func<EditTemplateDialogViewModel> _editFactory;
    private readonly TemplateSourceUpdater? _updater;
    // v1.0.0.x:每条菜单动作打 debug 日志(subsystem "template-mgmt")。
    // 测试 ctor 不传 → null 安全路径。生产 DI 在 MainViewModel.ShowTemplateManagement 注入。
    private readonly AppLogger? _logger;

    public ObservableCollection<TemplateConfig> Templates { get; } = new();

    /// <summary>
    /// v1.0.0.x: 模板下载/更新实时日志面板 — <c>TemplateSourceUpdater</c> 通过
    /// <see cref="IProgress{T}"/> 推 stdout 行,VM 捕到 UI SyncContext 自动 marshal,
    /// 跟 v0.6.18.4 BulkUpdateView Console 同模式。View Border Visibility 绑 <see cref="IsConsoleVisible"/>。
    /// 每行格式:<c>[{Kind}] {原 line}</c>(前缀区分多模板并发)。
    /// </summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();

    // v1.0.0.x:用户主动点 ✕ 关闭 Console 时置 true。下次 Start() / 一次新点击时复位 false。
    private bool _userHiddenConsole;

    /// <summary>
    /// v1.0.0.x: Console 面板可见性 — 有内容就显示,直到用户点 ✕ 关闭(用户意图优先)。
    /// 三态 = !_userHiddenConsole && ConsoleLog.Count > 0。同 v0.6.18.4 BulkUpdate 模式
    /// (那里 IsBusy 也参与,这里没有 IsBusy 概念 — 单次点击 fire-and-forget)。
    /// </summary>
    [JsonIgnore]
    public bool IsConsoleVisible => !_userHiddenConsole && ConsoleLog.Count > 0;

    /// <summary>
    /// v1.0.0.x: 点 Console 面板 ✕ 按钮时调 — 清空 + 隐藏。下次点击模板时
    /// 自动重新出现(<see cref="ConsoleLog"/> 重新追加)。
    /// </summary>
    public void ClearConsoleLog()
    {
        ConsoleLog.Clear();
        _userHiddenConsole = true;
        RaisePropertyChanged(nameof(IsConsoleVisible));
    }

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UpdateSourceCommand { get; }
    public ICommand DownloadOrUpdateCommand { get; }

    /// <summary>
    /// v1.0.0 hotfix: View layer (TemplateManagementView) subscribes to this event
    /// to instantiate + ShowDialog() the EditTemplateDialog window. Without this
    /// subscription the dialog never opens — click [+ 添加模板] silently no-ops.
    /// T10 originally wired ShowDialogRequested on EditTemplateDialogViewModel
    /// directly but no View subscribed; routed through this VM as the canonical
    /// subscription surface so the View only needs one DataContext hook.
    /// </summary>
    public event Action<EditTemplateDialogViewModel>? ShowEditDialogRequested;

    public TemplateManagementViewModel(
        Settings settings,
        Func<EditTemplateDialogViewModel>? editTemplateFactory,
        TemplateSourceUpdater? updater,
        AppLogger? logger = null)
    {
        _settings = settings;
        _logger = logger;
        // T14: wire cloneFunc from _updater (production-wiring for GitHub-mode Save).
        // null cloneFunc in unit tests (no updater provided) lets GitHub-mode tests use
        // their own mock via the 3-param ctor overload.
        _editFactory = editTemplateFactory ?? (() => new EditTemplateDialogViewModel(
            _settings,
            null,
            cloneFunc: _updater == null
                ? null
                : (repo, target, ct) => _updater.CloneAsync(repo, target, null, ct)));
        _updater = updater;

        foreach (var kvp in _settings.Templates)
        {
            // v1.0.0.x:本地目录状态 badge — 用 Settings.SystemTemplateLibraryDir 作 anchor
            // (内置模板 LocalSourceDir 是相对路径,需要 anchor 解析为绝对)。空 anchor
            // 时 TemplatePathResolver 自动 fallback 到 AppContext.BaseDirectory,跟
            // git clone target 一致(不变量 — clone 写到哪,badge 就检查哪)。
            kvp.Value.LocalDirMissing = !kvp.Value.LocalDirExists(_settings.SystemTemplateLibraryDir);
            Templates.Add(kvp.Value);
        }

        // v1.0.0.x: Console 行追加 → 通知 IsConsoleVisible 重算(log 数 0→>0 时变 true)。
        ConsoleLog.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset
                || (e.NewItems is { Count: > 0 } && ConsoleLog.Count == e.NewItems.Count))
            {
                RaisePropertyChanged(nameof(IsConsoleVisible));
            }
        };

        AddCommand = new RelayCommand(_ => AddTemplate());
        EditCommand = new RelayCommand(
            p => EditTemplate(p as TemplateConfig),
            p => p is TemplateConfig);
        DeleteCommand = new RelayCommand(
            p => DeleteTemplate(p as TemplateConfig),
            p => p is TemplateConfig tc && !IsBuiltIn(tc.Kind));
        UpdateSourceCommand = new RelayCommand(
            p => UpdateTemplateSource(p as TemplateConfig),
            p => p is TemplateConfig);
        // v1.0.0.x: 一键下载/更新 — 目标目录不存在则 clone,存在则 wipe + clone (即 pull)。
        // 区别于 UpdateSourceCommand(UpdateAsync pull-only,目录不存在会报错)。
        DownloadOrUpdateCommand = new RelayCommand(
            p => DownloadOrUpdateTemplateSource(p as TemplateConfig),
            p => p is TemplateConfig);
    }

    public bool IsBuiltIn(string kind) => kind == "ComfyUI";

    private void AddTemplate()
    {
        _logger?.Info("template-mgmt", "添加模板:打开对话框");
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Add;
        ShowEditDialogRequested?.Invoke(vm);
        if (vm.AppliedToSettings)
        {
            var c = vm.WorkingConfig;
            _logger?.Info("template-mgmt",
                $"添加模板已应用: kind='{c.Kind}' name='{c.Name}' sourceKind={c.SourceKind} repoUrl='{c.GitHubRepoUrl}' localDir='{c.LocalSourceDir}'");
            c.LocalDirMissing = !c.LocalDirExists(_settings.SystemTemplateLibraryDir);
            Templates.Add(c);
            _settings.Templates[c.Kind] = c;
        }
        else
        {
            _logger?.Info("template-mgmt", "添加模板已取消");
        }
    }

    private void EditTemplate(TemplateConfig? t)
    {
        if (t == null) return;
        _logger?.Info("template-mgmt", $"编辑模板: kind='{t.Kind}' name='{t.Name}' 打开对话框");
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Edit;
        vm.LoadFrom(t);
        ShowEditDialogRequested?.Invoke(vm);
        if (vm.AppliedToSettings)
        {
            var c = vm.WorkingConfig;
            _logger?.Info("template-mgmt",
                $"编辑模板已应用: kind='{c.Kind}' name='{c.Name}' sourceKind={c.SourceKind} repoUrl='{c.GitHubRepoUrl}' localDir='{c.LocalSourceDir}'");
            c.LocalDirMissing = !c.LocalDirExists(_settings.SystemTemplateLibraryDir);
            _settings.Templates[c.Kind] = c;
            var idx = Templates.IndexOf(t);
            if (idx >= 0) Templates[idx] = c;
        }
        else
        {
            _logger?.Info("template-mgmt", $"编辑模板已取消: kind='{t.Kind}'");
        }
    }

    private void DeleteTemplate(TemplateConfig? t)
    {
        if (t == null) return;
        if (IsBuiltIn(t.Kind))
        {
            _logger?.Warn("template-mgmt", $"拒绝删除内置模板: kind='{t.Kind}' name='{t.Name}'");
            return;
        }
        _logger?.Info("template-mgmt", $"删除模板: kind='{t.Kind}' name='{t.Name}' localDir='{t.LocalSourceDir}'");
        _settings.Templates.Remove(t.Kind);
        Templates.Remove(t);
    }

    private async void UpdateTemplateSource(TemplateConfig? t)
    {
        if (t == null) return;
        if (_updater == null)
        {
            _logger?.Warn("template-mgmt", $"更新源码 skipped (updater 未注入): kind='{t.Kind}'");
            return;
        }

        // Resolve URL based on SourceKind:
        //   GitHub templates use their configured repo URL.
        //   Local templates use GetDefaultRepoUrl only for built-in ComfyUI/Forge/SwarmUI;
        //   custom Local templates have no remote and are skipped silently.
        var url = t.SourceKind == TemplateSourceKind.GitHub
            ? t.GitHubRepoUrl
            : GetDefaultRepoUrl(t.Kind);
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger?.Info("template-mgmt", $"更新源码 skipped (无 repo URL): kind='{t.Kind}' sourceKind={t.SourceKind}");
            return;
        }

        _userHiddenConsole = false;  // 新 run 复位用户上次的 ✕ 隐藏
        var kind = t.Kind;
        var progress = new Progress<string>(line => ConsoleLog.Add($"[{kind}] {line}"));
        ConsoleLog.Add($"[{kind}] 开始更新源码: {url} → {t.LocalSourceDir}");
        _logger?.Info("template-mgmt",
            $"更新源码 启动: kind='{kind}' sourceKind={t.SourceKind} url='{url}' target='{t.LocalSourceDir}'");

        try
        {
            var result = await _updater.UpdateAsync(t.LocalSourceDir, url, progress, default).ConfigureAwait(true);
            if (result.Success)
            {
                ConsoleLog.Add($"[{kind}] ✓ 更新完成");
                _logger?.Info("template-mgmt", $"更新源码 完成: kind='{kind}'");
                // v1.0.0.x (2026-08-31): 重新计算 LocalDirMissing + 替换 Templates 中的项
                // —— 触发 ObservableCollection.Replace → WPF 重新评估该 card 的 binding,
                // 「源码未下载」amber badge 在 git pull 完成后自动消失。TemplateConfig
                // 是 POCO 无 INPC,直接改 t.LocalDirMissing 不会触发 PropertyChanged;
                // 必须替换项才能让 XAML 重新渲染(跟 AddTemplate/EditTemplate 同 pattern)。
                RefreshLocalDirMissing(t);
            }
            else
            {
                ConsoleLog.Add($"[{kind}] ✗ 失败: {result.Reason}");
                _logger?.Warn("template-mgmt", $"更新源码 失败: kind='{kind}' error='{result.Reason}'");
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"[{kind}] ✗ 异常: {ex.Message}");
            _logger?.Error("template-mgmt", $"更新源码 异常: kind='{kind}'", ex);
        }
    }

    private async void DownloadOrUpdateTemplateSource(TemplateConfig? t)
    {
        if (t == null) return;
        if (_updater == null)
        {
            _logger?.Warn("template-mgmt", $"下载与更新 skipped (updater 未注入): kind='{t.Kind}'");
            return;
        }
        var url = t.SourceKind == TemplateSourceKind.GitHub
            ? t.GitHubRepoUrl
            : GetDefaultRepoUrl(t.Kind);
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger?.Info("template-mgmt", $"下载与更新 skipped (无 repo URL): kind='{t.Kind}' sourceKind={t.SourceKind}");
            return;
        }
        _userHiddenConsole = false;  // 新 run 复位用户上次的 ✕ 隐藏
        var kind = t.Kind;
        var progress = new Progress<string>(line => ConsoleLog.Add($"[{kind}] {line}"));
        ConsoleLog.Add($"[{kind}] 开始下载/更新: {url} → {t.LocalSourceDir}");
        _logger?.Info("template-mgmt",
            $"下载与更新 启动: kind='{kind}' sourceKind={t.SourceKind} url='{url}' target='{t.LocalSourceDir}'");

        try
        {
            var result = await _updater.DownloadOrUpdateAsync(url, t.LocalSourceDir, progress, default).ConfigureAwait(true);
            if (result.Success)
            {
                ConsoleLog.Add($"[{kind}] ✓ 完成");
                _logger?.Info("template-mgmt", $"下载与更新 完成: kind='{kind}'");
                // v1.0.0.x (2026-08-31): 跟 UpdateTemplateSource 同 — git clone 完成后
                // 重新评估本地目录状态 + 触发 ObservableCollection.Replace 让 XAML 重新
                // 渲染该 card(amber「源码未下载」badge 自动消失)。
                RefreshLocalDirMissing(t);
            }
            else
            {
                ConsoleLog.Add($"[{kind}] ✗ 失败: {result.Reason}");
                _logger?.Warn("template-mgmt", $"下载与更新 失败: kind='{kind}' error='{result.Reason}'");
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"[{kind}] ✗ 异常: {ex.Message}");
            _logger?.Error("template-mgmt", $"下载与更新 异常: kind='{kind}'", ex);
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-08-31): 重新计算 <see cref="TemplateConfig.LocalDirMissing"/> 并
    /// 替换 <see cref="Templates"/> 中的项 ——
    /// <list type="number">
    ///   <item>UpdateSourceCommand / DownloadOrUpdateCommand 完成后,本地目录可能已 clone
    ///     出来(之前是 LocalDirMissing=true),需要刷新成 false</item>
    ///   <item>TemplateConfig 是 POCO 无 INPC,改字段不触发 PropertyChanged;
    ///     ObservableCollection 替换项触发 Replace 事件 → WPF 重新评估该 card 的所有 binding,
    ///     amber「源码未下载」badge Visibility 跟着 LocalDirMissing 重新求值自动消失</item>
    ///   <item>如果 Template 不在 Templates 集合(例如 ctor 之前调)— 静默 no-op</item>
    /// </list>
    /// 跟 AddTemplate / EditTemplate 中 line 148 / 171 的模式完全一致。
    /// </summary>
    private void RefreshLocalDirMissing(TemplateConfig t)
    {
        t.LocalDirMissing = !t.LocalDirExists(_settings.SystemTemplateLibraryDir);
        var idx = Templates.IndexOf(t);
        if (idx >= 0)
        {
            Templates[idx] = t;  // ObservableCollection.Replace → WPF re-render card
        }
    }

    private static string GetDefaultRepoUrl(string kind) => kind switch
    {
        "ComfyUI" => "https://github.com/comfyanonymous/ComfyUI.git",
        // v1.0.0.x: A1111 模板已下线,这里不再返回 AUTOMATIC1111 repo URL —
        // Stability-AI/stablediffusion 仓库已从 github 移除,即便本地有 A1111 env
        // 也无法 git clone 上游源码。Forge 用 huggingface_guess 替代 SD core,继续维护。
        // v1.0.0.x (2026-08-29): SwarmUI 模板已下线 — ProcessLauncher Python 假设对
        // SwarmUI functional break,这里不再返回 StableSwarmUI repo URL。
        "Forge" => "https://github.com/lllyasviel/stable-diffusion-webui-forge.git",
        // v1.0.0.x (2026-08-29): 4 个 GitHub-clone 视频/图像生成模板 seed —
        // HunyuanVideo (腾讯混元视频)、LTX-Video (Lightricks)、CogVideoX (智谱)、
        // Fooocus (lllyasviel 的 Focus 改良 SDXL WebUI)。用户改 URL 后不会回退
        // 到这里的 default(参考 TemplateConfigDefaults 的 seed-only 语义)。
        "HunyuanVideo" => "https://github.com/Tencent-Hunyuan/HunyuanVideo.git",
        // v1.0.0.x LTX-2 (T1):v1 Lightricks/LTX-Video repo 已弃用,默认 URL 指向
        // v2 monorepo Lightricks/LTX-2。跟 TemplateConfigDefaults.LTXVideo.GitHubRepoUrl
        // 对齐(用户首次创建 LTXVideo env / TemplateManagementView 点 Reset 时)。
        "LTXVideo" => "https://github.com/Lightricks/LTX-2.git",
        "CogVideoX" => "https://github.com/THUDM/CogVideo.git",
        "Fooocus" => "https://github.com/lllyasviel/Fooocus.git",
        // v1.0.0.x (2026-08-29): HivisionIDPhotos(Zeyi-Lin) AI 证件照 Gradio app。
        "HivisionIDPhotos" => "https://github.com/Zeyi-Lin/HivisionIDPhotos.git",
        _ => "",
    };
}

// EditTemplateDialogMode and EditTemplateDialogViewModel were moved to
// src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs (T10).
