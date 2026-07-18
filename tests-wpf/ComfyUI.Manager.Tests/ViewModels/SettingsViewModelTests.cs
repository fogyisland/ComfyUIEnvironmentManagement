using System;
using System.Collections.Generic;
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

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public RefreshResult NextResult { get; set; } = RefreshResult.Ok(0);
        public int CallCount { get; private set; }

        public FakeRefreshService()
            : base(new NullFetcher(),
                   new CatalogRepository(new CatalogCacheStore(System.IO.Path.Combine(
                       System.IO.Path.GetTempPath(),
                       $"null-repo-{System.Guid.NewGuid():N}.db"))),
                   new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            System.Threading.CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(NextResult);
        }

        private sealed class NullFetcher : CatalogFetcher
        {
            public NullFetcher() : base(
                new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60) { }
            public override Task<List<CatalogEntry>> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
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

        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());

        Assert.Equal("en_US", vm.Language);
        Assert.Equal("dark", vm.ThemeMode);
        Assert.Equal(120, vm.CacheTtlMinutes);
    }

    [Fact]
    public void LanguageSet_PersistsToFile()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
        vm.Language = "en_US";

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("en_US", reloaded.Language);
    }

    [Fact]
    public void Defaults_LoadsQuerySourcesAndDownloadSources_FromAppliedDefaults()
    {
        // 全新 settings.json → 走 SettingsDefaults 兜底,两个列表各 1 条 "comfyui manager"
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());

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
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[0]);

        Assert.Empty(vm.QuerySources);
        Assert.Null(vm.ActiveQuerySource);
    }

    [Fact]
    public void SwitchActive_PersistsImmediately()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService());
        vm.NewDownloadSourceName = "gh-proxy";
        vm.NewDownloadSourceUrl = "https://gh-proxy.com/{node}";

        vm.IsAddDownloadSourceOpen = true;
        vm.ConfirmAddDownloadSourceCommand.Execute(null);

        Assert.Equal(2, vm.DownloadSources.Count);
        Assert.Equal("gh-proxy", vm.DownloadSources[1].Name);
        Assert.Same(vm.DownloadSources[1], vm.ActiveDownloadSource);
    }

    [Fact]
    public void RefreshCatalogCommand_CallsService()
    {
        var svc = new FakeRefreshService();
        var vm = new SettingsViewModel(
            new SettingsRepository(_path), GitProxyConfig.Disabled, svc);

        vm.RefreshCatalogCommand.Execute(null);
        System.Threading.Thread.Sleep(50);  // wait for fire-and-forget

        Assert.Equal(1, svc.CallCount);
    }

    [Fact]
    public void RefreshCatalogCommand_Success_SetsStatusMessage()
    {
        var svc = new FakeRefreshService { NextResult = RefreshResult.Ok(50) };
        var vm = new SettingsViewModel(
            new SettingsRepository(_path), GitProxyConfig.Disabled, svc);

        vm.RefreshCatalogCommand.Execute(null);
        System.Threading.Thread.Sleep(50);

        Assert.Contains("刷新成功,共 50 个条目", vm.StatusMessage);
    }
}
