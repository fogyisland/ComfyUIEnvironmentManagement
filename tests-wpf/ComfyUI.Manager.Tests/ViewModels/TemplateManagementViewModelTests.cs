using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class TemplateManagementViewModelTests
{
    private static Settings SeedSettings() => new()
    {
        Templates = new Dictionary<string, TemplateConfig>
        {
            ["ComfyUI"] = new TemplateConfig { Name = "ComfyUI", Kind = "ComfyUI", LocalSourceDir = "Templates/ComfyUI", EntryScript = "main.py", EntryArgs = "--port {port}", ModelsSubdir = "models" },
            ["A1111"] = new TemplateConfig { Name = "A1111", Kind = "A1111", LocalSourceDir = "Templates/A1111", EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion" },
            ["MySwarm"] = new TemplateConfig { Name = "MySwarm", Kind = "MySwarm", LocalSourceDir = "D:/swarmui", EntryScript = "launch.sh", EntryArgs = "--listen", ModelsSubdir = "models" },
        },
    };

    [Fact]
    public void Ctor_LoadsAllTemplatesFromSettings()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.Equal(3, vm.Templates.Count);
        Assert.Contains(vm.Templates, t => t.Kind == "ComfyUI");
        Assert.Contains(vm.Templates, t => t.Kind == "A1111");
        Assert.Contains(vm.Templates, t => t.Kind == "MySwarm");
    }

    [Fact]
    public void DeleteCommand_CustomTemplate_RemovesFromSettings()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");
        vm.DeleteCommand.Execute(custom);
        Assert.Equal(2, vm.Templates.Count);
        Assert.False(s.Templates.ContainsKey("MySwarm"));
    }

    [Fact]
    public void DeleteCommand_BuiltInTemplate_Blocked()
    {
        // G13: built-in ComfyUI/A1111 cannot be deleted
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");
        vm.DeleteCommand.Execute(comfy);
        Assert.Equal(3, vm.Templates.Count);
        Assert.True(s.Templates.ContainsKey("ComfyUI"));
    }

    [Fact]
    public void IsBuiltIn_ComfyUIAndA1111_True_OtherFalse()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.True(vm.IsBuiltIn("ComfyUI"));
        Assert.True(vm.IsBuiltIn("A1111"));
        Assert.False(vm.IsBuiltIn("MySwarm"));
    }
}
