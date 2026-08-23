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
        vm.RequestShowDialog();
        if (vm.AppliedToSettings)
        {
            Templates.Add(vm.WorkingConfig);
            _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
        }
    }

    private void EditTemplate(TemplateConfig? t)
    {
        if (t == null) return;
        var vm = _editFactory();
        vm.Mode = EditTemplateDialogMode.Edit;
        vm.LoadFrom(t);
        vm.RequestShowDialog();
        if (vm.AppliedToSettings)
        {
            _settings.Templates[vm.WorkingConfig.Kind] = vm.WorkingConfig;
            var idx = Templates.IndexOf(t);
            if (idx >= 0) Templates[idx] = vm.WorkingConfig;
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

// EditTemplateDialogMode and EditTemplateDialogViewModel were moved to
// src-wpf/ComfyUI.Manager/ViewModels/EditTemplateDialogViewModel.cs (T10).
