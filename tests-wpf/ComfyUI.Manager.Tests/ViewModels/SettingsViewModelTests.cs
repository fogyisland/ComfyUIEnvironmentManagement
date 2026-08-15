using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
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

        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));

        Assert.Equal("en_US", vm.Language);
        Assert.Equal("dark", vm.ThemeMode);
        Assert.Equal(120, vm.CacheTtlMinutes);
    }

    [Fact]
    public void LanguageSet_PersistsToFile()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
        vm.Language = "en_US";
        vm.SaveCommand.Execute(null);

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("en_US", reloaded.Language);
    }

    // v0.6.7.1: ComfyUI 启动就绪超时 setter 持久化。
    [Fact]
    public void ComfyUiStartupTimeoutSeconds_SetPersists()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
        Assert.Equal(600, vm.ComfyUiStartupTimeoutSeconds);  // 默认

        vm.ComfyUiStartupTimeoutSeconds = 900;
        Assert.Equal(900, vm.ComfyUiStartupTimeoutSeconds);
        vm.SaveCommand.Execute(null);

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal(900, reloaded.ComfyUiStartupTimeoutSeconds);
    }

    [Fact]
    public void Defaults_LoadsQuerySourcesAndDownloadSources_FromAppliedDefaults()
    {
        // 全新 settings.json → 走 SettingsDefaults 兜底,两个列表各 1 条 "comfyui manager"
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));

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
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
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
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[0]);

        Assert.Empty(vm.QuerySources);
        Assert.Null(vm.ActiveQuerySource);
    }

    [Fact]
    public void SwitchActive_PersistsImmediately()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
        vm.NewQuerySourceName = "alt";
        vm.NewQuerySourceUrl = "https://alt/catalog.json";
        vm.ConfirmAddQuerySourceCommand.Execute(null);
        // active = "alt" now (auto-set on add)

        // switch back to first
        vm.ActiveQuerySource = vm.QuerySources[0];
        vm.SaveCommand.Execute(null);

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("comfyui manager", reloaded.ActiveQuerySourceName);
    }

    [Fact]
    public void ConfirmAddDownloadSourceCommand_AppendsAndSetsActive()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), HttpProxyConfig.Disabled, new FakeValidator(isValid: true));
        vm.NewDownloadSourceName = "gh-proxy";
        vm.NewDownloadSourceUrl = "https://gh-proxy.com/{node}";

        vm.IsAddDownloadSourceOpen = true;
        vm.ConfirmAddDownloadSourceCommand.Execute(null);

        Assert.Equal(2, vm.DownloadSources.Count);
        Assert.Equal("gh-proxy", vm.DownloadSources[1].Name);
        Assert.Same(vm.DownloadSources[1], vm.ActiveDownloadSource);
    }
    [Fact]
    public void ActivePythonInterpreter_ReturnsNull_WhenNameNotInList()
    {
        var s = new Settings
        {
            PythonInterpreters = new() { new() { Name = "py3.10", Path = "/x/3.10/python.exe" } },
            ActivePythonInterpreterName = "non-existent",
        };
        var (sut, _, _) = MakeVm(settings: s);
        Assert.Null(sut.ActivePythonInterpreter);
    }

    [Fact]
    public async Task AddPythonInterpreter_WithValidPath_WritesAndActivates()
    {
        var validator = new FakeValidator(isValid: true, version: "3.10.18");
        var (sut, repo, _) = MakeVm(validator: validator);
        sut.NewPythonInterpreterName = "py3.10";
        sut.NewPythonInterpreterPath = "/path/to/python.exe";
        await sut.ConfirmAddPythonInterpreterAsync();
        Assert.Single(sut.PythonInterpreters);
        Assert.Equal("py3.10", sut.PythonInterpreters[0].Name);
        Assert.Equal("/path/to/python.exe", sut.PythonInterpreters[0].Path);
        Assert.Equal("py3.10", sut.ActivePythonInterpreterName);
        Assert.Equal(2, repo.SaveCount);
        Assert.False(sut.IsAddPythonInterpreterOpen);
    }

    [Fact]
    public async Task AddPythonInterpreter_WithInvalidPath_ShowsError_DoesNotWrite()
    {
        var validator = new FakeValidator(isValid: false, error: "不是合法 Python 解释器");
        var (sut, repo, _) = MakeVm(validator: validator);
        sut.NewPythonInterpreterName = "bad";
        sut.NewPythonInterpreterPath = "/notepad.exe";
        await sut.ConfirmAddPythonInterpreterAsync();
        Assert.Empty(sut.PythonInterpreters);
        Assert.Equal("不是合法 Python 解释器", sut.AddPythonInterpreterError);
        Assert.True(sut.IsAddPythonInterpreterOpen);
        Assert.Equal(1, repo.SaveCount);
    }

    [Fact]
    public void RemovePythonInterpreter_ResetsActive_WhenActiveRemoved()
    {
        var s = new Settings
        {
            PythonInterpreters = new()
            {
                new() { Name = "py3.10", Path = "/3.10/python.exe" },
                new() { Name = "py3.11", Path = "/3.11/python.exe" },
            },
            ActivePythonInterpreterName = "py3.10",
        };
        var (sut, _, _) = MakeVm(settings: s);
        sut.RemovePythonInterpreterCommand.Execute(s.PythonInterpreters[0]);
        Assert.Single(sut.PythonInterpreters);
        Assert.Equal("py3.11", sut.ActivePythonInterpreterName);
    }

    private (SettingsViewModel vm, FakeSettingsRepository repo, Settings settings) MakeVm(
        Settings? settings = null, IPythonInterpreterValidator? validator = null)
    {
        var repo = new FakeSettingsRepository();
        if (settings is null)
        {
            // 默认 Settings 模板会让 SettingsDefaults 迁移插一条默认解释器;
            // PythonInterpreter 相关测试要"干净起跑",把模板路径清空避开迁移。
            settings = new Settings
            {
                TemplatePythonDir = "",
                DefaultPythonVersion = "",
            };
        }
        validator ??= new FakeValidator(isValid: true);
        var vm = new SettingsViewModel(repo, HttpProxyConfig.Disabled, validator, settings);
        return (vm, repo, settings);
    }

    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        private readonly bool _isValid;
        private readonly string _version;
        private readonly string _error;
        public FakeValidator(bool isValid, string version = "", string error = "")
        { _isValid = isValid; _version = version; _error = error; }
        public Task<ValidationResult> ValidateAsync(string path, CancellationToken ct = default)
        {
            return Task.FromResult(_isValid ? new ValidationResult(true, Version: _version) : new ValidationResult(false, Error: _error));
        }
    }

    private sealed class FakeSettingsRepository : SettingsRepository
    {
        public int SaveCount { get; private set; }
        private Settings _stored = new();
        public FakeSettingsRepository() : base(Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.json")) { }
        public override Settings Load() => _stored;
        public override void Save(Settings s)
        {
            SaveCount++;
            _stored = s;
        }
    }
}
