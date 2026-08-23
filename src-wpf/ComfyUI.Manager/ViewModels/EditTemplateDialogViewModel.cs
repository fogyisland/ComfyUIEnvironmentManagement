using System;
using System.Linq;
using System.Windows.Input;
using ComfyUI.Manager.Models;

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
        Action<EditTemplateDialogViewModel>? showDialogImpl)
    {
        _settings = settings;
        _showDialogImpl = showDialogImpl;
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

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(WorkingConfig.Name) &&
        !string.IsNullOrWhiteSpace(WorkingConfig.Kind) &&
        (Mode == EditTemplateDialogMode.Edit || !_settings.Templates.ContainsKey(WorkingConfig.Kind));

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
        };
        RaisePropertyChanged(nameof(WorkingConfig));
        // Refresh proxy properties (they read WorkingConfig.X) and CanSave
        RaisePropertyChanged(nameof(Name));
        RaisePropertyChanged(nameof(Kind));
        RaisePropertyChanged(nameof(LocalSourceDir));
        RaisePropertyChanged(nameof(EntryScript));
        RaisePropertyChanged(nameof(EntryArgs));
        RaisePropertyChanged(nameof(ModelsSubdir));
        RaisePropertyChanged(nameof(UserExtraArgs));
        RaisePropertyChanged(nameof(CanSave));
    }

    private void Save()
    {
        if (Mode == EditTemplateDialogMode.Edit && _originalKind != WorkingConfig.Kind)
        {
            _settings.Templates.Remove(_originalKind);
        }
        _settings.Templates[WorkingConfig.Kind] = WorkingConfig;
        AppliedToSettings = true;
    }

    private void Cancel()
    {
        AppliedToSettings = false;
    }
}
