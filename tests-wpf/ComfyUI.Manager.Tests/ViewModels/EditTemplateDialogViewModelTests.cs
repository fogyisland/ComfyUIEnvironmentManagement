using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
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
            Name = "Forge", Kind = "Forge", LocalSourceDir = "Templates/Forge",
            EntryScript = "webui.py", EntryArgs = "--port {port}", ModelsSubdir = "models/Stable-diffusion",
        };
        vm.LoadFrom(existing);
        Assert.Equal("Forge", vm.WorkingConfig.Name);
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

    // T10 R1: XAML TwoWay bindings write through VM proxy properties (not WorkingConfig directly).
    // Without the proxies, no PropertyChanged fires, so SaveCommand.CanExecute stays false even
    // when Name + Kind are valid — Save button appears permanently disabled in the running GUI.
    [Fact]
    public void SaveCommand_CanExecute_FollowsCanSaveReactivity()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        // Simulate XAML textbox input via proxy properties (not direct WorkingConfig mutation)
        vm.Name = "MySwarm";
        vm.Kind = "MySwarm";
        vm.LocalSourceDir = "D:/swarmui";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void SaveCommand_CanExecute_FalseWhenNameEmpty()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.Name = "";  // empty
        vm.Kind = "MySwarm";
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // --- T14: SourceKind switching ---

    [Fact]
    public void SourceKind_DefaultIs_Local()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        Assert.Equal(TemplateSourceKind.Local, vm.WorkingConfig.SourceKind);
        Assert.Equal("", vm.WorkingConfig.GitHubRepoUrl);
    }

    [Fact]
    public void SourceKind_Setter_WritesThroughProxy()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.SourceKind = TemplateSourceKind.GitHub;
        Assert.Equal(TemplateSourceKind.GitHub, vm.WorkingConfig.SourceKind);
        vm.SourceKind = TemplateSourceKind.Local;
        Assert.Equal(TemplateSourceKind.Local, vm.WorkingConfig.SourceKind);
    }

    [Fact]
    public void GitHubRepoUrl_SwitchToGitHub_AutoDerives_LocalSourceDir()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.GitHubRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git";
        vm.SourceKind = TemplateSourceKind.GitHub;
        Assert.Equal("ComfyUI", vm.WorkingConfig.LocalSourceDir);
    }

    [Fact]
    public void GitHubRepoUrl_NoGitSuffix_DerivesBasenameOnly()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.SourceKind = TemplateSourceKind.GitHub;
        vm.GitHubRepoUrl = "https://github.com/foo/MyRepo";
        Assert.Equal("MyRepo", vm.WorkingConfig.LocalSourceDir);
    }

    [Fact]
    public void GitHubRepoUrl_AlreadyHasLocalSourceDir_DoesNotOverwrite()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.SourceKind = TemplateSourceKind.GitHub;
        vm.LocalSourceDir = "MyCustomDir";
        vm.GitHubRepoUrl = "https://github.com/foo/Whatever.git";
        Assert.Equal("MyCustomDir", vm.WorkingConfig.LocalSourceDir);
    }

    // --- T14: CanSave branches ---

    [Fact]
    public void CanSave_LocalMode_EmptyLocalSourceDir_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.LocalSourceDir = "";  // empty
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_GitHubMode_EmptyUrl_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.GitHub;
        vm.WorkingConfig.GitHubRepoUrl = "";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_GitHubMode_InvalidUrlPrefix_False()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.GitHub;
        vm.WorkingConfig.GitHubRepoUrl = "ftp://server/repo.git";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void CanSave_GitHubMode_ValidInputs_True()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.GitHub;
        vm.WorkingConfig.GitHubRepoUrl = "https://github.com/foo/MyTpl.git";
        Assert.True(vm.CanSave);
    }

    // --- T14: SaveCommand GitHub mode clone integration ---

    [Fact]
    public void SaveCommand_GitHubMode_CallsCloneFunc_WithDerivedTargetDir()
    {
        var s = SeedSettings();
        string? calledRepo = null;
        string? calledTarget = null;
        Func<string, string, CancellationToken, Task<NodeOperationResult>> cloneFunc =
            (repo, target, ct) => { calledRepo = repo; calledTarget = target; return Task.FromResult(NodeOperationResult.Ok(null)); };

        var vm = new EditTemplateDialogViewModel(s, null, cloneFunc) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.GitHub;
        vm.WorkingConfig.GitHubRepoUrl = "https://github.com/foo/MyTpl.git";

        vm.SaveCommand.Execute(null);

        Assert.Equal("https://github.com/foo/MyTpl.git", calledRepo);
        Assert.Equal("MyTpl", calledTarget);
        Assert.True(vm.AppliedToSettings);
        Assert.True(s.Templates.ContainsKey("MyTpl"));
    }

    [Fact]
    public void SaveCommand_GitHubMode_CloneFails_DoesNotApplyToSettings()
    {
        var s = SeedSettings();
        Func<string, string, CancellationToken, Task<NodeOperationResult>> cloneFunc =
            (repo, target, ct) => Task.FromResult(NodeOperationResult.Fail("目标目录已存在:MyTpl"));

        var vm = new EditTemplateDialogViewModel(s, null, cloneFunc) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.GitHub;
        vm.WorkingConfig.GitHubRepoUrl = "https://github.com/foo/MyTpl.git";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.AppliedToSettings);
        Assert.False(s.Templates.ContainsKey("MyTpl"));
    }

    [Fact]
    public void SaveCommand_LocalMode_DoesNotCallCloneFunc()
    {
        var s = SeedSettings();
        int cloneCallCount = 0;
        Func<string, string, CancellationToken, Task<NodeOperationResult>> cloneFunc =
            (repo, target, ct) => { cloneCallCount++; return Task.FromResult(NodeOperationResult.Ok(null)); };

        var vm = new EditTemplateDialogViewModel(s, null, cloneFunc) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Name = "MyTpl";
        vm.WorkingConfig.Kind = "MyTpl";
        vm.WorkingConfig.SourceKind = TemplateSourceKind.Local;  // explicit Local
        vm.WorkingConfig.LocalSourceDir = "D:/localrepo";

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, cloneCallCount);
        Assert.True(vm.AppliedToSettings);
        Assert.True(s.Templates.ContainsKey("MyTpl"));
    }

    // --- v1.0.0.x: MetaRaw proxy + LoadFrom deep-copy ---

    [Fact]
    public void MetaRaw_Get_ReflectsWorkingConfigMeta()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.WorkingConfig.Meta["category"] = "图像生成";
        vm.WorkingConfig.Meta["description"] = "节点式 SD";
        Assert.Equal("category=图像生成\ndescription=节点式 SD", vm.MetaRaw);
    }

    [Fact]
    public void MetaRaw_Set_ParsesAndUpdatesWorkingConfigMeta()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.MetaRaw = "category=AI 语音\nnotes=tag1,tag2";
        Assert.Equal(2, vm.WorkingConfig.Meta.Count);
        Assert.Equal("AI 语音", vm.WorkingConfig.Meta["category"]);
        Assert.Equal("tag1,tag2", vm.WorkingConfig.Meta["notes"]);
    }

    [Fact]
    public void MetaRaw_Set_IgnoresBlankAndMalformedLines()
    {
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
        vm.MetaRaw = "category=A\n\n  \n=orphan\nnoequals\ndescription=B";
        Assert.Equal(2, vm.WorkingConfig.Meta.Count);
        Assert.True(vm.WorkingConfig.Meta.ContainsKey("category"));
        Assert.True(vm.WorkingConfig.Meta.ContainsKey("description"));
    }

    [Fact]
    public void LoadFrom_EditMode_CopiesMetaDeep()
    {
        // 防 LoadFrom 后用户改 MetaRaw 影响原对象 — deep-copy 必需。
        var s = SeedSettings();
        var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Edit };
        var existing = new TemplateConfig
        {
            Name = "Forge", Kind = "Forge",
            Meta = new Dictionary<string, string> { ["author"] = "AUTOMATIC1111" },
        };
        vm.LoadFrom(existing);
        Assert.Equal("author=AUTOMATIC1111", vm.MetaRaw);

        // 用户改 MetaRaw
        vm.MetaRaw = "author=我的版本";
        // 原 existing.Meta 不应被改
        Assert.Single(existing.Meta);
        Assert.Equal("AUTOMATIC1111", existing.Meta["author"]);
    }
}
