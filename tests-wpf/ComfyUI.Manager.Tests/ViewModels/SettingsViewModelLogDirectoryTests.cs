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
/// <see cref="GitProxyConfig"/> + <see cref="IPythonInterpreterValidator"/>,所以测试
/// 走同款 temp .json repo(既有 <c>SettingsViewModelTests</c> 同 pattern) +
/// <c>GitProxyConfig.Disabled</c> + <c>FakeValidator</c>。
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
        return new SettingsViewModel(repo, GitProxyConfig.Disabled, new FakeValidator(isValid: true));
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
        var vm = CreateVm(new Settings());
        Assert.Equal("", vm.LogDirectory);
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        public FakeValidator(bool isValid) { _isValid = isValid; }
        private readonly bool _isValid;
        public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new ValidationResult(_isValid, _isValid ? "ok" : "bad"));
    }
}
