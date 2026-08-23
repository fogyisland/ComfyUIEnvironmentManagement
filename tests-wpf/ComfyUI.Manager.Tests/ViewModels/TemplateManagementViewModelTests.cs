using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
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

    // --- T16: UpdateSourceCommand branches + CanUpdateSource ---

    [Fact]
    public void UpdateSourceCommand_LocalKind_BuiltInComfyUI_CallsUpdateAsync_WithDefaultUrl()
    {
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.UpdateSourceCommand.Execute(comfy);

        Assert.Equal(1, fakeUpdater.CallCount);
        Assert.Equal("Templates/ComfyUI", fakeUpdater.LastTargetDir);
        Assert.Equal("https://github.com/comfyanonymous/ComfyUI.git", fakeUpdater.LastRepoUrl);
    }

    [Fact]
    public void UpdateSourceCommand_LocalKind_CustomTemplate_DoesNotCallUpdateAsync()
    {
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");

        vm.UpdateSourceCommand.Execute(custom);

        Assert.Equal(0, fakeUpdater.CallCount);  // no default URL for custom Kind
    }

    [Fact]
    public void UpdateSourceCommand_GitHubKind_CallsUpdateAsync_WithConfigRepoUrl()
    {
        var s = SeedSettings();
        s.Templates["GithubTpl"] = new TemplateConfig
        {
            Name = "GithubTpl", Kind = "GithubTpl",
            LocalSourceDir = "Templates/GithubTpl",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/user/GithubTpl.git",
        };
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var gh = vm.Templates.First(t => t.Kind == "GithubTpl");

        vm.UpdateSourceCommand.Execute(gh);

        Assert.Equal(1, fakeUpdater.CallCount);
        Assert.Equal("Templates/GithubTpl", fakeUpdater.LastTargetDir);
        Assert.Equal("https://github.com/user/GithubTpl.git", fakeUpdater.LastRepoUrl);
    }

    [Fact]
    public void UpdateSourceCommand_NullUpdater_DoesNotThrow()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        // Should silently skip, not throw
        var ex = Record.Exception(() => vm.UpdateSourceCommand.Execute(comfy));
        Assert.Null(ex);
    }

    /// <summary>
    /// T16 test helper: subclass TemplateSourceUpdater and override UpdateAsync to capture
    /// call args without invoking the real GitRunner. Base ctor creates a GitRunner that's
    /// never invoked (we override the method that uses it), so no network/disk side effects.
    /// </summary>
    private class FakeUpdater : TemplateSourceUpdater
    {
        public int CallCount { get; private set; }
        public string? LastTargetDir { get; private set; }
        public string? LastRepoUrl { get; private set; }
        public IProgress<string>? LastProgress { get; private set; }

        public FakeUpdater() : base("git") { }

        public override Task<NodeOperationResult> UpdateAsync(
            string targetDir, string repoUrl,
            IProgress<string>? progress, CancellationToken ct)
        {
            CallCount++;
            LastTargetDir = targetDir;
            LastRepoUrl = repoUrl;
            LastProgress = progress;
            return Task.FromResult(NodeOperationResult.Ok(null));
        }
    }
}
