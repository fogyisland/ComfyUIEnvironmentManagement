using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class SettingsViewModelTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModelTests()
    {
        _path = Path.Combine(
            Path.GetTempPath(), $"comfy-settings-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_PopulatesSettingsFromFile()
    {
        var repo = new SettingsRepository(_path);
        repo.Save(new ComfyUI.Manager.Models.Settings
        {
            Language = "en_US",
            ThemeMode = "dark",
            CatalogCacheTtlMinutes = 120,
        });

        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);

        Assert.Equal("en_US", vm.Language);
        Assert.Equal("dark", vm.ThemeMode);
        Assert.Equal(120, vm.CacheTtlMinutes);
    }

    [Fact]
    public void LanguageSet_PersistsToFile()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.Language = "en_US";

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("en_US", reloaded.Language);
    }

    [Fact]
    public void Defaults_LoadsQuerySourcesAndDownloadSources_FromAppliedDefaults()
    {
        // 全新 settings.json → 走 SettingsDefaults 兜底,两个列表各 1 条 "comfyui manager"
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);

        Assert.Single(vm.QuerySources);
        Assert.Equal("comfyui manager", vm.QuerySources[0].Name);
        Assert.Single(vm.DownloadSources);
        Assert.Equal("comfyui manager", vm.DownloadSources[0].Name);
        Assert.Equal("comfyui manager", vm.ActiveQuerySource?.Name);
        Assert.Equal("comfyui manager", vm.ActiveDownloadSource?.Name);
    }

    [Fact]
    public void ConfirmAddQuerySourceCommand_AppendsAndSetsActive()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "my-mirror";
        vm.NewQuerySourceUrl = "https://my-mirror/catalog.json";

        vm.IsAddQuerySourceOpen = true;
        vm.ConfirmAddQuerySourceCommand.Execute(null);

        Assert.Equal(2, vm.QuerySources.Count);
        Assert.Equal("my-mirror", vm.QuerySources[1].Name);
        Assert.Same(vm.QuerySources[1], vm.ActiveQuerySource);
        Assert.False(vm.IsAddQuerySourceOpen);
        Assert.Equal("", vm.NewQuerySourceName);
        Assert.Equal("", vm.NewQuerySourceUrl);
    }

    [Fact]
    public void ConfirmAddQuerySourceCommand_EmptyFields_DoesNothing()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "";
        vm.NewQuerySourceUrl = "";
        vm.IsAddQuerySourceOpen = true;

        vm.ConfirmAddQuerySourceCommand.Execute(null);

        Assert.Single(vm.QuerySources);  // 没追加
        Assert.False(vm.IsAddQuerySourceOpen);  // 仍然关闭(等价于取消)
    }

    [Fact]
    public void RemoveQuerySourceCommand_WhenActive_FallsBackToFirst()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        // 默认只有 1 条,先加一条自定义并切到它
        vm.NewQuerySourceName = "my-mirror";
        vm.NewQuerySourceUrl = "https://my-mirror/catalog.json";
        vm.ConfirmAddQuerySourceCommand.Execute(null);
        // 现在 active = "my-mirror"

        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[1]);

        Assert.Single(vm.QuerySources);
        Assert.Equal("comfyui manager", vm.ActiveQuerySource?.Name);
    }

    [Fact]
    public void RemoveQuerySourceCommand_LastOne_LeavesListEmpty()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[0]);

        Assert.Empty(vm.QuerySources);
        Assert.Null(vm.ActiveQuerySource);
    }

    [Fact]
    public void SwitchActive_PersistsImmediately()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "alt";
        vm.NewQuerySourceUrl = "https://alt/catalog.json";
        vm.ConfirmAddQuerySourceCommand.Execute(null);
        // active = "alt" now (auto-set on add)

        // switch back to first
        vm.ActiveQuerySource = vm.QuerySources[0];

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("comfyui manager", reloaded.ActiveQuerySourceName);
    }

    [Fact]
    public void ConfirmAddDownloadSourceCommand_AppendsAndSetsActive()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewDownloadSourceName = "gh-proxy";
        vm.NewDownloadSourceUrl = "https://gh-proxy.com/{node}";

        vm.IsAddDownloadSourceOpen = true;
        vm.ConfirmAddDownloadSourceCommand.Execute(null);

        Assert.Equal(2, vm.DownloadSources.Count);
        Assert.Equal("gh-proxy", vm.DownloadSources[1].Name);
        Assert.Same(vm.DownloadSources[1], vm.ActiveDownloadSource);
    }
}