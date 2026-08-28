using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.12:SettingsViewModel.LogDirectory 绑定测试。setter 写回 Settings 内字段。
/// <para>
/// <see cref="SettingsViewModel"/> ctor 需要 <see cref="SettingsRepository"/> +
/// <see cref="HttpProxyConfig"/> + <see cref="IPythonInterpreterValidator"/>,所以测试
/// 走同款 temp .json repo(既有 <c>SettingsViewModelTests</c> 同 pattern) +
/// <c>HttpProxyConfig.Disabled</c> + <c>FakeValidator</c>。
/// </para>
/// <para>
/// <see cref="SettingsViewModel"/> ctor 调 <see cref="SettingsDefaults.Apply"/>,
/// 它在 <see cref="Settings.LogDirectory"/> 非空时尝试 <c>Directory.CreateDirectory</c>
/// (失败静默)。所以默认空路径测试不需要建子目录;非空测试用合法 temp dir。
/// </para>
/// </summary>
public class SettingsViewModelLogDirectoryTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModelLogDirectoryTests()
    {
        _path = Path.Combine(
            Path.GetTempPath(), $"comfy-settings-logdir-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private SettingsViewModel CreateVm(Settings settings)
    {
        // 把测试 settings 写到 disk 再让 VM 通过 repo.Load() 拿,
        // 跟既有 SettingsViewModelTests 同 pattern(VM ctor 走 _repo.Load())。
        var repo = new SettingsRepository(_path);
        repo.Save(settings);
        return new SettingsViewModel(repo, HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
    }

    [Fact]
    public void LogDirectory_Getter_ReturnsSettingsValue()
    {
        var vm = CreateVm(new Settings { LogDirectory = @"D:\foo" });
        Assert.Equal(@"D:\foo", vm.LogDirectory);
    }

    [Fact]
    public void LogDirectory_Setter_WritesBackToSettings()
    {
        var vm = CreateVm(new Settings());
        vm.LogDirectory = @"D:\bar";
        // VM 的 setter 只改 in-memory _settings + MarkDirty;磁盘持久化要 SaveCommand。
        // 跟既有 SettingsViewModelTests (ComfyUiStartupTimeoutSeconds_SetPersists) 同 pattern。
        Assert.Equal(@"D:\bar", vm.LogDirectory);
        Assert.True(vm.HasUnsavedChanges);
        vm.SaveCommand.Execute(null);

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal(@"D:\bar", reloaded.LogDirectory);
    }

    [Fact]
    public void LogDirectory_DefaultEmpty()
    {
        // v1.0.0.x 用户原话"日志目录也列出绝对路径,目录为 logs" ——
        // LogDirectory 改 ResolveAsAbsolute,空 → seed 当前 projectRoot + "logs"
        // 的绝对路径。SettingsRepository 临时文件在 Path.GetTempPath()下,所以
        // VM ctor 调 SettingsDefaults.Apply 后 LogDirectory 是 temp 根 + "logs"。
        // 此 测试改为确认非空 + 包含 "logs" 子目录名(种子逻辑走绝对路径)。
        var vm = CreateVm(new Settings());
        Assert.NotEqual("", vm.LogDirectory);
        Assert.EndsWith("logs", vm.LogDirectory);
        Assert.True(Path.IsPathRooted(vm.LogDirectory));
    }

    // —— v0.6.12 hotfix:BrowseLogDirectoryCommand 测试 ——
    // 走 FolderDialogOverride seam(跟 v0.6.5.19 MessageBoxOverride 同 pattern)。
    // 不调真 OpenFolderDialog(STA 模态阻塞测试线程)。
    [Fact]
    public void BrowseLogDirectoryCommand_UserSelectsFolder_WritesLogDirectory()
    {
        var vm = CreateVm(new Settings());
        const string picked = @"D:\my-custom-logs";
        string? passedInitialPath = "sentinel";
        vm.FolderDialogOverride = initial =>
        {
            passedInitialPath = initial;
            return picked;
        };

        vm.BrowseLogDirectoryCommand.Execute(null);

        Assert.Equal(picked, vm.LogDirectory);
        Assert.True(vm.HasUnsavedChanges);
        // v1.0.0.x:SettingsDefaults.Apply 后 LogDirectory 不再是空,传 VM 当前值作 initial
        Assert.NotEqual("", passedInitialPath);
        Assert.True(Path.IsPathRooted(passedInitialPath));
    }

    [Fact]
    public void BrowseLogDirectoryCommand_UserCancels_DoesNotChangeLogDirectory()
    {
        var vm = CreateVm(new Settings { LogDirectory = @"D:\existing" });
        vm.FolderDialogOverride = _ => null;  // 用户点取消

        vm.BrowseLogDirectoryCommand.Execute(null);

        Assert.Equal(@"D:\existing", vm.LogDirectory);
        Assert.False(vm.HasUnsavedChanges);
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        public FakeValidator(bool isValid) { _isValid = isValid; }
        private readonly bool _isValid;
        public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new ValidationResult(_isValid, _isValid ? "ok" : "bad"));
    }
}
