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

public sealed class SettingsViewModelSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _settingsPath;

    public SettingsViewModelSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "settingsvmshared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _settingsPath = Path.Combine(_rootDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private SettingsViewModel BuildVm()
    {
        var repo = new TestSettingsRepo(_settingsPath);
        var proxy = new GitProxyConfig();
        var validator = new FakePythonInterpreterValidator();
        return new SettingsViewModel(repo, proxy, validator);
    }

    [Fact]
    public void SharedModelsDirectory_DefaultIsEmpty()
    {
        var vm = BuildVm();
        Assert.Equal("", vm.SharedModelsDirectory);
    }

    [Fact]
    public void SharedModelsDirectory_SetterPersists()
    {
        var vm = BuildVm();
        vm.SharedModelsDirectory = @"D:\Models\shared";
        Assert.Equal(@"D:\Models\shared", vm.SharedModelsDirectory);
        // Reload from disk
        var repo2 = new TestSettingsRepo(_settingsPath);
        var fresh = repo2.Load();
        Assert.Equal(@"D:\Models\shared", fresh.SharedModelsDirectory);
    }
}

/// <summary>类似既有 TestSettingsRepo 模式 — 写一个简单测试用 repo(继承生产类即可重用 Save/Load 路径)。</summary>
internal sealed class TestSettingsRepo : SettingsRepository
{
    public TestSettingsRepo(string path) : base(path) { }
    public Settings ReloadFresh() => base.Load();
}

internal sealed class FakePythonInterpreterValidator : IPythonInterpreterValidator
{
    public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new ValidationResult(IsValid: true));
}