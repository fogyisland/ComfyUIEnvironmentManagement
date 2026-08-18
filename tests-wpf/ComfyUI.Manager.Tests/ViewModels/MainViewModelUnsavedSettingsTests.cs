using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.11+ SDD B T3:主窗口关闭 guard。Settings 有未保存改动时弹 3 按钮
/// MessageBox(是=保存退出/否=丢弃退出/取消=留下),test seam UnsavedPromptOverride
/// 防 STA 死锁。
/// </summary>
public sealed class MainViewModelUnsavedSettingsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dbPath;
    private readonly string _rootDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;

    public MainViewModelUnsavedSettingsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "mvm-unsaved-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _settingsPath = Path.Combine(_rootDir, "settings.json");
        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _settingsRepo = new SettingsRepository(_settingsPath);
        _settings = _settingsRepo.Load();
        // 注入默认 Settings,不让 ctor 从 disk 加载
        SettingsDefaults.Apply(_settings, _rootDir);
        _settingsRepo.Save(_settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private MainViewModel NewMvm() => new MainViewModel(
        _factory,
        null!, null!, null!, null!, null!,
        _settingsRepo, HttpProxyConfig.Disabled, _settings,
        null!, null!, null!, null!, null!, null!, null!,
        "", _rootDir,
        null!, null!, new UiPreferencesService(_rootDir))
    {
        // 绕开 WPF STA:SettingsViewFactory 返回 null 不构造真实 View,
        // 只让 _settingsViewModel 缓存建出来供 ConfirmDiscardUnsavedSettings 读。
        SettingsViewFactory = _ => null,
    };

    [Fact]
    public void ConfirmDiscard_NoSettingsVm_ReturnsTrue()
    {
        var mvm = NewMvm();
        // CurrentView 是 Dashboard,CurrentSettingsViewModel 是 null
        var result = mvm.ConfirmDiscardUnsavedSettings();
        Assert.True(result);
    }

    [Fact]
    public void ConfirmDiscard_Clean_ReturnsTrue_NoPrompt()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);    // 缓存 + 切到 Settings tab
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());    // 没 dirty → 直接允许关闭
    }

    [Fact]
    public void ConfirmDiscard_Dirty_SaveChoice_PersistsAndReturnsTrue()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "save-me";   // 标 dirty

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Save;
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());

        var fresh = _settingsRepo.Load();
        Assert.Equal("save-me", fresh.DefaultModelsDirectory);
        Assert.False(svm.HasUnsavedChanges);
    }

    [Fact]
    public void ConfirmDiscard_Dirty_DiscardChoice_RevertsAndReturnsTrue()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "discarded-value";

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Discard;
        Assert.True(mvm.ConfirmDiscardUnsavedSettings());

        Assert.False(svm.HasUnsavedChanges);
        Assert.Equal("", svm.DefaultModelsDirectory);   // disk 默认值
        // disk 没动
        var fresh = _settingsRepo.Load();
        Assert.Equal("", fresh.DefaultModelsDirectory);
    }

    [Fact]
    public void ConfirmDiscard_Dirty_CancelChoice_ReturnsFalse_KeepsDirty()
    {
        var mvm = NewMvm();
        mvm.ShowSettingsCommand.Execute(null);
        var svm = mvm.CurrentSettingsViewModel!;
        svm.DefaultModelsDirectory = "keep-dirty";

        mvm.UnsavedPromptOverride = _ => MainViewModel.UnsavedChoice.Cancel;
        Assert.False(mvm.ConfirmDiscardUnsavedSettings());

        Assert.True(svm.HasUnsavedChanges);
        Assert.Equal("keep-dirty", svm.DefaultModelsDirectory);
        var fresh = _settingsRepo.Load();
        Assert.Equal("", fresh.DefaultModelsDirectory);   // 没写盘
    }
}
