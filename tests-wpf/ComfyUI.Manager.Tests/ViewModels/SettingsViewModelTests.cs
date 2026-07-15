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
}