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
