using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0 multi-template: sidebar page VM. Lists + adds + edits + deletes templates.
/// Built-in ComfyUI + A1111 are protected from delete (G13).
/// </summary>
public class TemplateManagementViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly Func<EditTemplateDialogViewModel> _editFactory;
    private readonly TemplateSourceUpdater? _updater;

    public ObservableCollection<TemplateConfig> Templates { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UpdateSourceCommand { get; }

    public TemplateManagementViewModel(
        Settings settings,
        Func<EditTemplateDialogViewModel>? editTemplateFactory,
        TemplateSourceUpdater? updater)
    {
        _settings = settings;
        _editFactory = editTemplateFactory ?? (() => new EditTemplateDialogViewModel(_settings, null));
        _updater = updater;

        foreach (var kvp in _settings.Templates)
            Templates.Add(kvp.Value);

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
    }

    public bool IsBuiltIn(string kind) => kind == "ComfyUI" || kind == "A1111";

    private void AddTemplate()
    {
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Add;
        if (vm.ShowDialogRequested != null)
        {
            vm.ShowDialogRequested.Invoke(vm);
            if (vm.AppliedToSettings)
            {
                Templates.Add(vm.WorkingConfig);
                _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
            }
        }
    }

    private void EditTemplate(TemplateConfig? t)
    {
        if (t == null) return;
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Edit;
        vm.LoadFrom(t);
        if (vm.ShowDialogRequested != null)
        {
            vm.ShowDialogRequested.Invoke(vm);
            if (vm.AppliedToSettings)
            {
                _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
                var idx = Templates.IndexOf(t);
                if (idx >= 0) Templates[idx] = vm.WorkingConfig;
            }
        }
    }

    private void DeleteTemplate(TemplateConfig? t)
    {
        if (t == null || IsBuiltIn(t.Kind)) return;
        _settings.Templates.Remove(t.Kind);
        Templates.Remove(t);
    }

    private void UpdateTemplateSource(TemplateConfig? t)
    {
        if (t == null || _updater == null) return;
        _ = _updater.UpdateAsync(t.LocalSourceDir, GetDefaultRepoUrl(t.Kind), null, default);
    }

    private static string GetDefaultRepoUrl(string kind) => kind switch
    {
        "ComfyUI" => "https://github.com/comfyanonymous/ComfyUI.git",
        "A1111" => "https://github.com/AUTOMATIC1111/stable-diffusion-webui.git",
        _ => "",
    };
}

// TODO T10: Replace with real dialog VM in T10. T8 only needs the public surface
// (Mode / WorkingConfig / AppliedToSettings / ShowDialogRequested / LoadFrom) so the
// TemplateManagementViewModel ctor compiles and T10 can layer in actual XAML binding.
public enum EditTemplateDialogMode
{
    Add,
    Edit,
}

// TODO T10: Replace stub with full XAML-bound dialog VM. Real impl will own the
// TextBox/BindingPipeline for Name/Kind/LocalSourceDir/EntryScript/EntryArgs/
// ModelsSubdir/ExtraJunctionTargets/UserExtraArgs + Apply/Cancel buttons. T8 only
// references the public surface — T10 will replace fields/properties with
// INotifyPropertyChanged-backed bindings to XAML controls.
public class EditTemplateDialogViewModel
{
    public EditTemplateDialogMode Mode { get; set; } = EditTemplateDialogMode.Add;
    public TemplateConfig WorkingConfig { get; set; } = new();
    public bool AppliedToSettings { get; set; }
    public Action<EditTemplateDialogViewModel>? ShowDialogRequested { get; set; }

    public EditTemplateDialogViewModel(Settings settings, AppLogger? logger)
    {
        // T8 stub: no-op. T10 will subscribe to settings/log when building the
        // real VM (e.g. ReadTemplateDefaults / ValidateConfig / SaveOnApply).
        _ = settings;
        _ = logger;
    }

    public void LoadFrom(TemplateConfig source)
    {
        // T8 stub: shallow clone of fields to keep caller mutation isolated.
        // T10 will wire this to bound controls via two-way bindings.
        WorkingConfig = new TemplateConfig
        {
            Name = source.Name,
            Kind = source.Kind,
            LocalSourceDir = source.LocalSourceDir,
            EntryScript = source.EntryScript,
            EntryArgs = source.EntryArgs,
            ModelsSubdir = source.ModelsSubdir,
            ExtraJunctionTargets = new List<string>(source.ExtraJunctionTargets),
            UserExtraArgs = source.UserExtraArgs,
        };
    }
}

// TODO T11: Replace stub with real TemplateSourceUpdater. T11 lands the actual
// git clone / wipe logic; T8 only needs the constructor + UpdateAsync signature
// so production wire-up compiles AND test path with `updater: null` passes.
public class TemplateSourceUpdater
{
    public Task<bool> UpdateAsync(string targetDir, string repoUrl, IProgress<string>? progress, CancellationToken ct)
    {
        // T8 stub: no-op success. T11 will wipe targetDir + git clone --depth=1.
        _ = targetDir;
        _ = repoUrl;
        _ = progress;
        _ = ct;
        return Task.FromResult(true);
    }
}
