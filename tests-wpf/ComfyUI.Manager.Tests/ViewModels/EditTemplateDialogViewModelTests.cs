using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EditTemplateDialogViewModelTests
{
    private static Settings SeedSettings() => new()
    {
        Templates = new Dictionary<string, TemplateConfig>
        {
            ["ComfyUI"] = new TemplateConfig { Name = "ComfyUI", Kind = "ComfyUI" },
        },
    };

    [Fact]
    public void Ctor_AddMode_EmptyWorkingConfig()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        Assert.Equal("", vm.WorkingConfig.Name);
        Assert.Equal("", vm.WorkingConfig.Kind);
    }

    [Fact]
    public void LoadFrom_EditMode_CopiesAllFields()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Edit };
        var existing = new TemplateConfig
        {
            Name = "A1111", Kind = "A1111", LocalSourceDir = "Templates/A1111",
            EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
        };
        vm.LoadFrom(existing);
        Assert.Equal("A1111", vm.WorkingConfig.Name);
        Assert.Equal("webui.py", vm.WorkingConfig.EntryScript);
        Assert.Equal("models/Stable-diffusion", vm.WorkingConfig.ModelsSubdir);
    }

    [Fact]
    public void CanSave_EmptyName_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "";
        vm.WorkingConfig.Kind = "ComfyUI";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_EmptyKind_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTemplate";
        vm.WorkingConfig.Kind = "";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_AddMode_DuplicateKind_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "ComfyUI";
        vm.WorkingConfig.Kind = "ComfyUI";  // already exists
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_AddMode_ValidInputs_True()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MySwarm";
        vm.WorkingConfig.Kind = "MySwarm";
        vm.WorkingConfig.LocalSourceDir = "D:/swarmui";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void SaveCommand_AddMode_AppliesToSettings()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MySwarm";
        vm.WorkingConfig.Kind = "MySwarm";
        vm.WorkingConfig.LocalSourceDir = "D:/swarmui";
        vm.SaveCommand.Execute(null);
        Assert.True(s.Templates.ContainsKey("MySwarm"));
        Assert.True(vm.AppliedToSettings);
    }
}
