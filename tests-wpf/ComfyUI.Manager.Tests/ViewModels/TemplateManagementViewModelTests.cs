using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    // --- v1.0.0.x: DownloadOrUpdateCommand (smart clone-or-update dispatch) ---

    [Fact]
    public void DownloadOrUpdateCommand_GitHubKind_CallsDownloadOrUpdateAsync_WithConfigUrl()
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

        vm.DownloadOrUpdateCommand.Execute(gh);

        Assert.Equal(1, fakeUpdater.DownloadCallCount);
        Assert.Equal("https://github.com/user/GithubTpl.git", fakeUpdater.LastDownloadUrl);
        Assert.Equal("Templates/GithubTpl", fakeUpdater.LastDownloadTarget);
    }

    [Fact]
    public void DownloadOrUpdateCommand_LocalKind_BuiltInComfyUI_CallsWithDefaultUrl()
    {
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.DownloadOrUpdateCommand.Execute(comfy);

        Assert.Equal(1, fakeUpdater.DownloadCallCount);
        Assert.Equal("https://github.com/comfyanonymous/ComfyUI.git", fakeUpdater.LastDownloadUrl);
        Assert.Equal("Templates/ComfyUI", fakeUpdater.LastDownloadTarget);
    }

    [Fact]
    public void DownloadOrUpdateCommand_LocalKind_CustomTemplate_DoesNotCall()
    {
        // Custom Local templates have no default repo URL — silently skip.
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");

        vm.DownloadOrUpdateCommand.Execute(custom);

        Assert.Equal(0, fakeUpdater.DownloadCallCount);
    }

    [Fact]
    public void DownloadOrUpdateCommand_NullUpdater_DoesNotThrow()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        var ex = Record.Exception(() => vm.DownloadOrUpdateCommand.Execute(comfy));
        Assert.Null(ex);
    }

    // --- v1.0.0 hotfix: ShowEditDialogRequested event wiring ---

    [Fact]
    public void AddCommand_RaisesShowEditDialogRequested_WithAddMode()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        EditTemplateDialogViewModel? raisedVm = null;
        vm.ShowEditDialogRequested += dlg => raisedVm = dlg;

        vm.AddCommand.Execute(null);

        Assert.NotNull(raisedVm);
        Assert.Equal(EditTemplateDialogMode.Add, raisedVm!.Mode);
    }

    [Fact]
    public void EditCommand_RaisesShowEditDialogRequested_WithEditMode()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");
        EditTemplateDialogViewModel? raisedVm = null;
        vm.ShowEditDialogRequested += dlg => raisedVm = dlg;

        vm.EditCommand.Execute(custom);

        Assert.NotNull(raisedVm);
        Assert.Equal(EditTemplateDialogMode.Edit, raisedVm!.Mode);
    }

    // --- v1.0.0.x: debug logs (subsystem "template-mgmt") at each menu action ---

    /// <summary>
    /// Helper: creates a real AppLogger writing to a temp dir; returns logger + log lines
    /// captured via ReadLines(). AppLogger is sealed → can't subclass, so use real instance
    /// pointed at temp dir for assertion.
    /// </summary>
    private static (AppLogger logger, Func<string[]> ReadLines) MakeTempLogger()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "template-mgmt-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logger = new AppLogger(tempDir);
        return (logger, () => logger.ReadLines());
    }

    [Fact]
    public void DeleteCommand_CustomTemplate_LogsDeleteToAppLogger()
    {
        var s = SeedSettings();
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null, logger: logger);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");

        vm.DeleteCommand.Execute(custom);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("删除模板") && l.Contains("MySwarm"));
    }

    [Fact]
    public void DeleteCommand_BuiltInTemplate_LogsWarnToAppLogger()
    {
        var s = SeedSettings();
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null, logger: logger);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.DeleteCommand.Execute(comfy);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("[WARN ]") && l.Contains("拒绝删除内置模板") && l.Contains("ComfyUI"));
    }

    [Fact]
    public void UpdateSourceCommand_GitHubKind_LogsStartWithUrl()
    {
        var s = SeedSettings();
        s.Templates["GithubTpl"] = new TemplateConfig
        {
            Name = "GithubTpl", Kind = "GithubTpl",
            LocalSourceDir = "Templates/GithubTpl",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/user/GithubTpl.git",
        };
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater, logger: logger);
        var gh = vm.Templates.First(t => t.Kind == "GithubTpl");

        vm.UpdateSourceCommand.Execute(gh);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("更新源码 启动") && l.Contains("GithubTpl") && l.Contains("https://github.com/user/GithubTpl.git"));
    }

    [Fact]
    public void UpdateSourceCommand_CustomLocalKind_LogsSkipped()
    {
        var s = SeedSettings();
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater, logger: logger);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");

        vm.UpdateSourceCommand.Execute(custom);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("更新源码 skipped") && l.Contains("无 repo URL") && l.Contains("MySwarm"));
        Assert.Equal(0, fakeUpdater.CallCount);
    }

    [Fact]
    public void UpdateSourceCommand_NullUpdater_LogsWarn()
    {
        var s = SeedSettings();
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null, logger: logger);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.UpdateSourceCommand.Execute(comfy);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("[WARN ]") && l.Contains("updater 未注入"));
    }

    [Fact]
    public void DownloadOrUpdateCommand_GitHubKind_LogsStartWithUrlAndTarget()
    {
        var s = SeedSettings();
        s.Templates["GithubTpl"] = new TemplateConfig
        {
            Name = "GithubTpl", Kind = "GithubTpl",
            LocalSourceDir = "Templates/GithubTpl",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/user/GithubTpl.git",
        };
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater, logger: logger);
        var gh = vm.Templates.First(t => t.Kind == "GithubTpl");

        vm.DownloadOrUpdateCommand.Execute(gh);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("下载与更新 启动") && l.Contains("GithubTpl") && l.Contains("https://github.com/user/GithubTpl.git") && l.Contains("Templates/GithubTpl"));
    }

    [Fact]
    public void DownloadOrUpdateCommand_CustomLocalKind_LogsSkipped()
    {
        var s = SeedSettings();
        var (logger, readLines) = MakeTempLogger();
        using var _ = logger;
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater, logger: logger);
        var custom = vm.Templates.First(t => t.Kind == "MySwarm");

        vm.DownloadOrUpdateCommand.Execute(custom);

        var lines = readLines();
        Assert.Contains(lines, l => l.Contains("[template-mgmt]") && l.Contains("下载与更新 skipped") && l.Contains("MySwarm"));
        Assert.Equal(0, fakeUpdater.DownloadCallCount);
    }

    [Fact]
    public void Ctor_WithNullLogger_DoesNotThrow()
    {
        // Backward compat: existing tests / production wiring may omit logger.
        var s = SeedSettings();
        var ex = Record.Exception(() => new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null, logger: null));
        Assert.Null(ex);
    }

    [Fact]
    public void AddCommand_NullLogger_DoesNotThrow()
    {
        // Logger=null path: AddCommand should still work (no NRE).
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null, logger: null);
        var ex = Record.Exception(() => vm.AddCommand.Execute(null));
        Assert.Null(ex);
    }

    /// <summary>
    /// T16 test helper: subclass TemplateSourceUpdater and override UpdateAsync to capture
    /// call args without invoking the real GitRunner. Base ctor creates a GitRunner that's
    /// never invoked (we override the method that uses it), so no network/disk side effects.
    /// v1.0.0.x: optionally report progress lines via <paramref name="progress"/> so ConsoleLog
    /// capture in VM can be tested.
    /// </summary>
    private class FakeUpdater : TemplateSourceUpdater
    {
        public int CallCount { get; private set; }
        public string? LastTargetDir { get; private set; }
        public string? LastRepoUrl { get; private set; }
        public IProgress<string>? LastProgress { get; private set; }

        // v1.0.0.x: track DownloadOrUpdateAsync calls separately
        public int DownloadCallCount { get; private set; }
        public string? LastDownloadUrl { get; private set; }
        public string? LastDownloadTarget { get; private set; }

        public FakeUpdater() : base("git") { }

        public override Task<NodeOperationResult> UpdateAsync(
            string targetDir, string repoUrl,
            IProgress<string>? progress, CancellationToken ct)
        {
            CallCount++;
            LastTargetDir = targetDir;
            LastRepoUrl = repoUrl;
            LastProgress = progress;
            progress?.Report("Cloning into 'target'...");
            progress?.Report("Receiving objects: 50% (100/200)");
            return Task.FromResult(NodeOperationResult.Ok(null));
        }

        public override Task<NodeOperationResult> DownloadOrUpdateAsync(
            string repoUrl, string targetDir,
            IProgress<string>? progress, CancellationToken ct)
        {
            DownloadCallCount++;
            LastDownloadUrl = repoUrl;
            LastDownloadTarget = targetDir;
            progress?.Report("Cloning into 'target'...");
            progress?.Report("Receiving objects: 50% (100/200)");
            return Task.FromResult(NodeOperationResult.Ok(null));
        }
    }

    // --- v1.0.0.x: Console panel behavior (ConsoleLog + IsConsoleVisible + ClearConsoleLog) ---

    [Fact]
    public void IsConsoleVisible_FalseInitially()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public void IsConsoleVisible_TrueAfterManualAddToConsoleLog()
    {
        // Manual add to ConsoleLog (simulating a progress line) should flip visibility.
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        Assert.False(vm.IsConsoleVisible);

        vm.ConsoleLog.Add("test line");

        Assert.True(vm.IsConsoleVisible);
    }

    [Fact]
    public async Task DownloadOrUpdateCommand_AddsProgressLinesToConsoleLog()
    {
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.DownloadOrUpdateCommand.Execute(comfy);

        // async void — give the SynchronizationContext a chance to marshal the lines.
        await Task.Yield();
        await Task.Delay(50);

        // Should see: start banner, FakeUpdater-reported "Cloning..." + "Receiving...", success banner.
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("开始下载/更新"));
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("Cloning into"));
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("Receiving objects"));
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("完成"));
    }

    [Fact]
    public async Task UpdateSourceCommand_AddsProgressLinesToConsoleLog()
    {
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.UpdateSourceCommand.Execute(comfy);

        await Task.Yield();
        await Task.Delay(50);

        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("开始更新源码"));
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("Cloning into"));
        Assert.Contains(vm.ConsoleLog, l => l.Contains("[ComfyUI]") && l.Contains("更新完成"));
    }

    [Fact]
    public void ClearConsoleLog_HidesConsole()
    {
        var s = SeedSettings();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: null);
        vm.ConsoleLog.Add("a");
        vm.ConsoleLog.Add("b");
        Assert.True(vm.IsConsoleVisible);

        vm.ClearConsoleLog();

        Assert.Empty(vm.ConsoleLog);
        Assert.False(vm.IsConsoleVisible);
    }

    [Fact]
    public async Task ClearConsoleLog_AfterDownload_RestoresVisibility_OnNextDownload()
    {
        // Simulate: user clicked 下载与更新 → lines → ✕ → next click → lines again.
        var s = SeedSettings();
        var fakeUpdater = new FakeUpdater();
        var vm = new TemplateManagementViewModel(s, editTemplateFactory: null, updater: fakeUpdater);
        var comfy = vm.Templates.First(t => t.Kind == "ComfyUI");

        vm.DownloadOrUpdateCommand.Execute(comfy);
        await Task.Yield();
        await Task.Delay(50);
        Assert.True(vm.IsConsoleVisible);

        vm.ClearConsoleLog();
        Assert.False(vm.IsConsoleVisible);

        vm.DownloadOrUpdateCommand.Execute(comfy);
        await Task.Yield();
        await Task.Delay(50);
        Assert.True(vm.IsConsoleVisible);  // _userHiddenConsole was reset by Start
    }
}
