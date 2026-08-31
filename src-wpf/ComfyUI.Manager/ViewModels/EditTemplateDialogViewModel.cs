using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;  // for NodeOperationResult

namespace ComfyUI.Manager.ViewModels;

public enum EditTemplateDialogMode { Add, Edit }

/// <summary>
/// v1.0.0 multi-template: add or edit a single TemplateConfig. Backed by Settings.Templates.
/// View layer wires the XAML to this VM and raises ShowDialogRequested to actually show the window.
/// </summary>
public class EditTemplateDialogViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly Action<EditTemplateDialogViewModel>? _showDialogImpl;
    private readonly Func<string, string, CancellationToken, Task<NodeOperationResult>>? _cloneFunc;
    private string _originalKind = "";  // for edit mode: tracks the original kind to handle rename

    public EditTemplateDialogMode Mode { get; set; } = EditTemplateDialogMode.Add;
    public TemplateConfig WorkingConfig { get; private set; } = new();
    public bool AppliedToSettings { get; private set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Raised when the view should show itself. The view implementation creates the Window
    /// and calls back into the VM (Save / Cancel) on user interaction.
    /// </summary>
    public event Action<EditTemplateDialogViewModel>? ShowDialogRequested;

    public EditTemplateDialogViewModel(
        Settings settings,
        Action<EditTemplateDialogViewModel>? showDialogImpl,
        Func<string, string, CancellationToken, Task<NodeOperationResult>>? cloneFunc = null)
    {
        _settings = settings;
        _showDialogImpl = showDialogImpl;
        _cloneFunc = cloneFunc;
        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>
    /// Caller invokes this to trigger the dialog. Internally raises ShowDialogRequested.
    /// The View layer subscribes to the event and shows the Window.
    /// </summary>
    public void RequestShowDialog()
    {
        ShowDialogRequested?.Invoke(this);
    }

    public bool CanSave
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkingConfig.Name)) return false;
            if (string.IsNullOrWhiteSpace(WorkingConfig.Kind)) return false;
            if (Mode == EditTemplateDialogMode.Add && _settings.Templates.ContainsKey(WorkingConfig.Kind)) return false;

            // Spec §9: 名称在所有 template 中唯一(不区分大小写)。编辑自己时
            // 排除自己(kind 可能改了,Name 也可能改了 — 但编辑前后都是同一条)。
            if (HasNameCollision()) return false;

            return WorkingConfig.SourceKind switch
            {
                TemplateSourceKind.Local => !string.IsNullOrWhiteSpace(WorkingConfig.LocalSourceDir),
                TemplateSourceKind.GitHub => !string.IsNullOrWhiteSpace(WorkingConfig.GitHubRepoUrl)
                                            && IsValidRepoUrl(WorkingConfig.GitHubRepoUrl),
                _ => false,
            };
        }
    }

    /// <summary>
    /// Spec §9 重复 name 校验: case-insensitive 比较。Add 模式任何同名 Name 都冲突;
    /// Edit 模式排除 _originalKind 自身(用户编辑时 Name 可能没改,Kind 可能改了 —
    /// 都要排除才能让当前条目通过校验)。
    /// </summary>
    private bool HasNameCollision()
    {
        var name = (WorkingConfig.Name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var kvp in _settings.Templates)
        {
            if (Mode == EditTemplateDialogMode.Edit && kvp.Key == _originalKind) continue;
            if (string.Equals(kvp.Value.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // T10 R1: XAML TwoWay bindings target these VM-level proxy properties instead of
    // WorkingConfig.X directly. TemplateConfig is a plain POCO without INPC, so writing
    // to WorkingConfig.Name/Kind/etc. from a binding never raises PropertyChanged, and
    // SaveCommand.CanExecute never re-evaluates as the user types. The setters fire both
    // the property's own PropertyChanged (to refresh any TwoWay readback) and CanSave
    // (to drive SaveCommand.RaiseCanExecuteChanged via the WPF CommandManager pipeline).
    public string Name
    {
        get => WorkingConfig.Name;
        set { if (WorkingConfig.Name != value) { WorkingConfig.Name = value; RaiseFor(nameof(Name)); } }
    }

    /// <summary>
    /// v1.0.0.x: <see cref="TemplateConfig.Meta"/> 字典的字符串视图 — 每行
    /// <c>key=value</c>,供 EditTemplateDialog 的 Multi-line TextBox 双向绑定。
    /// WPF 原生 <c>Binding</c> 对 <c>Dictionary&lt;,&gt;</c> 双绑不可靠,改用字符串
    /// setter 解析回 Dictionary。空行 / 没 <c>=</c> 行忽略。Set 时不抛异常 —
    /// 用户中途编辑到一半的中间态(<c>key=</c> 没 value)直接跳过,Save 时
    /// <see cref="LoadFrom"/> 反向会保留上次成功解析的 key。
    /// </summary>
    public string MetaRaw
    {
        get => WorkingConfig.MetaRaw;
        set
        {
            var parsed = TemplateConfig.ParseMetaRaw(value);
            // 比对当前 WorkingConfig.Meta;只有真的变了才回写 + 通知。
            if (!DictionariesEqual(WorkingConfig.Meta, parsed))
            {
                WorkingConfig.Meta = parsed;
                RaiseFor(nameof(MetaRaw));
            }
        }
    }

    private static bool DictionariesEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var v) || v != kvp.Value) return false;
        }
        return true;
    }

    public string Kind
    {
        get => WorkingConfig.Kind;
        set { if (WorkingConfig.Kind != value) { WorkingConfig.Kind = value; RaiseFor(nameof(Kind)); } }
    }

    /// <summary>
    /// Spec §5 + §9 G12: 编辑现有 built-in 模板(ComfyUI)时 Kind 不可改 —
    /// 通过 <see cref="EditTemplateDialog.xaml"/> ComboBox <c>IsEditable</c> 绑定禁用编辑。
    /// Add 模式始终可编辑(用户输入新 Kind);Edit 模式只在原 Kind 不是 built-in
    /// 时才可编辑。
    /// v1.0.0.x: A1111 模板已下线,IsBuiltInKind 收缩到只有 ComfyUI 一项。
    /// </summary>
    public bool IsKindEditable =>
        Mode == EditTemplateDialogMode.Add
        || !(Mode == EditTemplateDialogMode.Edit && IsBuiltInKind(_originalKind));

    private static bool IsBuiltInKind(string kind) =>
        kind == "ComfyUI";

    /// <summary>
    /// Spec §9 友好的 built-in 提示文本(内置即"ComfyUI"时给红字提示,
    /// 否则显示空 — 别在标签上再加 text)。
    /// </summary>
    public string KindReadOnlyHint =>
        (Mode == EditTemplateDialogMode.Edit && IsBuiltInKind(_originalKind))
            ? "(内置 Kind 不可修改)"
            : "";

    public string LocalSourceDir
    {
        get => WorkingConfig.LocalSourceDir;
        set { if (WorkingConfig.LocalSourceDir != value) { WorkingConfig.LocalSourceDir = value; RaiseFor(nameof(LocalSourceDir)); } }
    }

    public TemplateSourceKind SourceKind
    {
        get => WorkingConfig.SourceKind;
        set
        {
            if (WorkingConfig.SourceKind == value) return;
            WorkingConfig.SourceKind = value;
            // Switching to GitHub: auto-fill LocalSourceDir from URL basename if empty.
            if (value == TemplateSourceKind.GitHub
                && string.IsNullOrWhiteSpace(WorkingConfig.LocalSourceDir)
                && !string.IsNullOrWhiteSpace(WorkingConfig.GitHubRepoUrl))
            {
                WorkingConfig.LocalSourceDir = DeriveRepoBasename(WorkingConfig.GitHubRepoUrl);
                RaiseFor(nameof(LocalSourceDir));
            }
            RaiseFor(nameof(SourceKind));
        }
    }

    public string GitHubRepoUrl
    {
        get => WorkingConfig.GitHubRepoUrl;
        set
        {
            if (WorkingConfig.GitHubRepoUrl == value) return;
            WorkingConfig.GitHubRepoUrl = value;
            // In GitHub mode with empty LocalSourceDir, re-derive on URL change.
            if (WorkingConfig.SourceKind == TemplateSourceKind.GitHub
                && string.IsNullOrWhiteSpace(WorkingConfig.LocalSourceDir))
            {
                WorkingConfig.LocalSourceDir = DeriveRepoBasename(value);
                RaiseFor(nameof(LocalSourceDir));
            }
            RaiseFor(nameof(GitHubRepoUrl));
        }
    }

    // v1.0.0.x: removed 启动脚本/启动参数/模型子目录/用户附加参数 proxy properties.
    // These fields remain on TemplateConfig data model (backward compat for old settings.json),
    // LoadFrom still copies them into WorkingConfig, but UI no longer exposes them.
    // See EditTemplateDialog.xaml for the user-facing rationale.

    private void RaiseFor(string prop)
    {
        RaisePropertyChanged(prop);
        RaisePropertyChanged(nameof(CanSave));
        // WPF re-evaluates Command.CanExecute only when its CanExecuteChanged event fires.
        // PropertyChanged on CanSave alone does NOT trigger Button.IsEnabled re-poll, so we
        // must explicitly notify the command. This is the same pattern as
        // ModelMarketplaceViewModel / BulkUpdateViewModel / SettingsViewModel.
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void LoadFrom(TemplateConfig existing)
    {
        _originalKind = existing.Kind;
        WorkingConfig = new TemplateConfig
        {
            Name = existing.Name,
            Kind = existing.Kind,
            LocalSourceDir = existing.LocalSourceDir,
            EntryScript = existing.EntryScript,
            EntryArgs = existing.EntryArgs,
            ModelsSubdir = existing.ModelsSubdir,
            ExtraJunctionTargets = new System.Collections.Generic.List<string>(existing.ExtraJunctionTargets),
            UserExtraArgs = existing.UserExtraArgs,
            SourceKind = existing.SourceKind,
            GitHubRepoUrl = existing.GitHubRepoUrl,
            // v1.0.0.x: Meta 字典 deep-copy,避免 Edit 修改直接影响原对象。
            Meta = new Dictionary<string, string>(existing.Meta),
            FooocusEntryMode = existing.FooocusEntryMode,
        };
        RaisePropertyChanged(nameof(WorkingConfig));
        // Refresh proxy properties (they read WorkingConfig.X) and CanSave
        RaisePropertyChanged(nameof(Name));
        RaisePropertyChanged(nameof(Kind));
        RaisePropertyChanged(nameof(LocalSourceDir));
        RaisePropertyChanged(nameof(SourceKind));
        RaisePropertyChanged(nameof(GitHubRepoUrl));
        // v1.0.0.x: MetaRaw 也要通知 — 新建 dialog 时 LoadFrom 之前 MetaRaw 是空串,
        // 之后要刷成 WorkingConfig.MetaRaw(从源复制过来的字典序列化)。
        RaisePropertyChanged(nameof(MetaRaw));
        RaisePropertyChanged(nameof(CanSave));
        // G12: Mode + _originalKind 决定 Kind 是否可编辑 — LoadFrom 后必通知。
        RaisePropertyChanged(nameof(IsKindEditable));
        RaisePropertyChanged(nameof(KindReadOnlyHint));
    }

    private async void Save()
    {
        var cfg = WorkingConfig;
        if (cfg.SourceKind == TemplateSourceKind.GitHub)
        {
            // Ensure LocalSourceDir is filled from URL if user left it blank.
            if (string.IsNullOrWhiteSpace(cfg.LocalSourceDir) && !string.IsNullOrWhiteSpace(cfg.GitHubRepoUrl))
            {
                cfg.LocalSourceDir = DeriveRepoBasename(cfg.GitHubRepoUrl);
            }
            if (_cloneFunc == null)
            {
                // No clone wired (test path or misconfigured prod) — refuse to Save in GitHub mode.
                AppliedToSettings = false;
                return;
            }
            var result = await _cloneFunc(cfg.GitHubRepoUrl, cfg.LocalSourceDir, CancellationToken.None);
            if (!result.Success)
            {
                AppliedToSettings = false;
                return;
            }
        }

        if (Mode == EditTemplateDialogMode.Edit && _originalKind != cfg.Kind)
        {
            _settings.Templates.Remove(_originalKind);
        }
        _settings.Templates[cfg.Kind] = cfg;
        AppliedToSettings = true;
    }

    private void Cancel()
    {
        AppliedToSettings = false;
    }

    internal static bool IsValidRepoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DeriveRepoBasename(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var last = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }
        return last;
    }
}