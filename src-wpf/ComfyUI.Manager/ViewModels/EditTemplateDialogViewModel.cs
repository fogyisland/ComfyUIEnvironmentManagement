using System;
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

            return WorkingConfig.SourceKind switch
            {
                TemplateSourceKind.Local => !string.IsNullOrWhiteSpace(WorkingConfig.LocalSourceDir),
                TemplateSourceKind.GitHub => !string.IsNullOrWhiteSpace(WorkingConfig.GitHubRepoUrl)
                                            && IsValidRepoUrl(WorkingConfig.GitHubRepoUrl),
                _ => false,
            };
        }
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

    public string Kind
    {
        get => WorkingConfig.Kind;
        set { if (WorkingConfig.Kind != value) { WorkingConfig.Kind = value; RaiseFor(nameof(Kind)); } }
    }

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

    public string EntryScript
    {
        get => WorkingConfig.EntryScript;
        set { if (WorkingConfig.EntryScript != value) { WorkingConfig.EntryScript = value; RaiseFor(nameof(EntryScript)); } }
    }

    public string EntryArgs
    {
        get => WorkingConfig.EntryArgs;
        set { if (WorkingConfig.EntryArgs != value) { WorkingConfig.EntryArgs = value; RaiseFor(nameof(EntryArgs)); } }
    }

    public string ModelsSubdir
    {
        get => WorkingConfig.ModelsSubdir;
        set { if (WorkingConfig.ModelsSubdir != value) { WorkingConfig.ModelsSubdir = value; RaiseFor(nameof(ModelsSubdir)); } }
    }

    public string UserExtraArgs
    {
        get => WorkingConfig.UserExtraArgs;
        set { if (WorkingConfig.UserExtraArgs != value) { WorkingConfig.UserExtraArgs = value; RaiseFor(nameof(UserExtraArgs)); } }
    }

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
        };
        RaisePropertyChanged(nameof(WorkingConfig));
        // Refresh proxy properties (they read WorkingConfig.X) and CanSave
        RaisePropertyChanged(nameof(Name));
        RaisePropertyChanged(nameof(Kind));
        RaisePropertyChanged(nameof(LocalSourceDir));
        RaisePropertyChanged(nameof(SourceKind));
        RaisePropertyChanged(nameof(GitHubRepoUrl));
        RaisePropertyChanged(nameof(EntryScript));
        RaisePropertyChanged(nameof(EntryArgs));
        RaisePropertyChanged(nameof(ModelsSubdir));
        RaisePropertyChanged(nameof(UserExtraArgs));
        RaisePropertyChanged(nameof(CanSave));
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