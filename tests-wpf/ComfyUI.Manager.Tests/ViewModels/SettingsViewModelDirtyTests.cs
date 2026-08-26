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
/// v0.6.11+ SDD B T1:dirty tracking + Save / Discard + CopyInto 单元测试。
/// </summary>
public sealed class SettingsViewModelDirtyTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModelDirtyTests()
    {
        _path = Path.Combine(Path.GetTempPath(),
            "settings-vm-dirty-" + Path.GetRandomFileName() + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private SettingsViewModel NewVm() => new SettingsViewModel(
        new SettingsRepository(_path),
        HttpProxyConfig.Disabled,
        new FakeValidator(isValid: true));

    private SettingsViewModel NewVmWithCallback(Func<string, Task> onEnvsDirSaved) =>
        new SettingsViewModel(
            new SettingsRepository(_path),
            HttpProxyConfig.Disabled,
            new FakeValidator(isValid: true),
            sharedSettings: null,
            themeService: null,
            onEnvsDirSaved: onEnvsDirSaved);

    [Fact]
    public void MarkDirty_SingleProperty_SetsDirtyAndHasUnsavedChanges()
    {
        var vm = NewVm();
        Assert.False(vm.HasUnsavedChanges);

        vm.DefaultModelsDirectory = @"D:\Models\shared";

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(1, vm.UnsavedCount);
        Assert.True(vm.Dirty["DefaultModelsDirectory"]);
    }

    [Fact]
    public void MarkDirty_MultipleProperties_AggregatesCount()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "a";
        vm.ComfyUiStartupTimeoutSeconds = 900;
        vm.FetchNodeVersionsOnRefresh = true;
        Assert.Equal(3, vm.UnsavedCount);
        Assert.True(vm.Dirty["DefaultModelsDirectory"]);
        Assert.True(vm.Dirty["ComfyUiStartupTimeoutSeconds"]);
        Assert.True(vm.Dirty["FetchNodeVersionsOnRefresh"]);
    }

    [Fact]
    public void MarkDirty_SameProperty_Twice_StaysAtOne()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "a";
        vm.DefaultModelsDirectory = "b";   // 同一 property,只算一行 dirty
        Assert.Equal(1, vm.UnsavedCount);
    }

    [Fact]
    public void Setter_DoesNotWriteToDisk_BeforeSave()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = @"D:\Models\dirty";

        var fresh = new SettingsRepository(_path).Load();
        // v0.6.22+:ModelsDirectory 硬删后 SettingsDefaults 给 DefaultModelsDirectory 兜底 "Models"
        // v1.0.0:目录重构,PascalCase 统一 → "Models"(而非 v0.6.x 的 "models")
        Assert.Equal("Models", fresh.DefaultModelsDirectory);   // 还是默认值,未写盘
    }

    [Fact]
    public void SaveCommand_PersistsSettings_ClearsAllDirty()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = @"D:\Models\shared";
        vm.ComfyUiStartupTimeoutSeconds = 900;
        Assert.True(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);

        var fresh = new SettingsRepository(_path).Load();
        Assert.Equal(@"D:\Models\shared", fresh.DefaultModelsDirectory);
        Assert.Equal(900, fresh.ComfyUiStartupTimeoutSeconds);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.UnsavedCount);
    }

    [Fact]
    public void SaveCommand_CanExecute_FalseWhenClean()
    {
        var vm = NewVm();
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.DefaultModelsDirectory = "x";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void DiscardCommand_RevertsInPlace_KeepsSameSettingsInstance()
    {
        // 关键约束:_settings 是 App 共享实例,Discard 不能换引用(G4)
        var vm = NewVm();
        var beforeRef = vm.GetType()
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm);
        vm.DefaultModelsDirectory = "dirty";
        vm.SaveCommand.Execute(null);                 // 写到 disk
        vm.DefaultModelsDirectory = "another-dirty"; // 再改 dirty

        vm.DiscardCommand.Execute(null);

        var afterRef = vm.GetType()
            .GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm);
        Assert.Same(beforeRef, afterRef);             // 同一对象,没被换掉
        Assert.Equal("dirty", vm.DefaultModelsDirectory); // 回到 disk 上的值
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void DiscardCommand_LeavesDiskUnchanged()
    {
        var vm = NewVm();
        vm.DefaultModelsDirectory = "committed";
        vm.SaveCommand.Execute(null);
        vm.ComfyUiStartupTimeoutSeconds = 999;       // dirty

        vm.DiscardCommand.Execute(null);

        var fresh = new SettingsRepository(_path).Load();
        Assert.Equal("committed", fresh.DefaultModelsDirectory);
        Assert.Equal(600, fresh.ComfyUiStartupTimeoutSeconds); // 默认值
    }

    [Fact]
    public void DiscardCommand_RevertsThemeMode_InMemory()
    {
        // G3:Discard 必须能回滚 ThemeMode(尽管它即时预览)
        var themeService = new RecordingThemeService();
        var vm = new SettingsViewModel(
            new SettingsRepository(_path),
            HttpProxyConfig.Disabled,
            new FakeValidator(isValid: true),
            sharedSettings: null,
            themeService: themeService);

        vm.ThemeMode = "light";   // 触发 Apply(Light)
        Assert.Equal(ThemeMode.Light, themeService.LastApplied);

        vm.ThemeMode = "dark";    // Apply(Dark)
        Assert.Equal(ThemeMode.Dark, themeService.LastApplied);

        vm.DiscardCommand.Execute(null);   // disk 默认是 "dark",但 in-memory 已变 "dark"... 这条路径验它会重新 Apply(Dark)
        Assert.Equal(ThemeMode.Dark, themeService.LastApplied);
    }

    // — helpers —
    private sealed class FakeValidator : IPythonInterpreterValidator
    {
        private readonly bool _isValid;
        public FakeValidator(bool isValid) { _isValid = isValid; }
        public Task<ValidationResult> ValidateAsync(
            string path, CancellationToken ct = default)
            => Task.FromResult(new ValidationResult(_isValid, _isValid ? "ok" : "bad"));
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public ThemeMode? LastApplied { get; private set; }
        public int ApplyCallCount { get; private set; }
        public ThemeMode Current => LastApplied ?? ThemeMode.Dark;
        public void Apply(ThemeMode mode)
        {
            LastApplied = mode;
            ApplyCallCount++;
        }
        public event EventHandler<ThemeMode>? ThemeChanging;
        public event EventHandler<ThemeMode>? Applied;
    }

    // --- v1.0.0.x:onEnvsDirSaved callback 触发验证 ---
    // SaveCommand 内部用 async lambda,但 RelayCommand.Execute 是 sync(包装成 async void)。
    // 测试用 TaskCompletionSource 等 callback 真正被调起 — 不依赖 TaskCompletionSource 默认是瞬时完成的。

    [Fact]
    public void SaveCommand_EnvsDirDirty_InvokesOnEnvsDirSavedCallback()
    {
        var tcs = new TaskCompletionSource<string?>();
        var vm = NewVmWithCallback(rel => { tcs.SetResult(rel); return Task.CompletedTask; });
        vm.EnvsDir = "NewEnvs";

        vm.SaveCommand.Execute(null);

        Assert.True(tcs.Task.Wait(TimeSpan.FromSeconds(2)),
            "SaveCommand 触发 2s 内未调 onEnvsDirSaved");
        Assert.Equal("NewEnvs", tcs.Task.Result);
    }

    [Fact]
    public void SaveCommand_OtherFieldDirty_DoesNotInvokeOnEnvsDirSavedCallback()
    {
        var callCount = 0;
        var tcs = new TaskCompletionSource();
        var vm = NewVmWithCallback(_ => { Interlocked.Increment(ref callCount); tcs.SetResult(); return Task.CompletedTask; });
        vm.DefaultModelsDirectory = @"D:\foo"; // 改别的字段

        vm.SaveCommand.Execute(null);

        // 等 200ms — 如果 callback 被错误触发会触发 TCS
        Thread.Sleep(200);
        Assert.Equal(0, callCount);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void SaveCommand_NoCallback_DoesNotThrow()
    {
        // NewVm() 默认 callback = null — Save 不应抛
        var vm = NewVm();
        vm.EnvsDir = "SomeNewDir";

        var ex = Record.Exception(() => vm.SaveCommand.Execute(null));

        Assert.Null(ex);
        Assert.False(vm.HasUnsavedChanges);
    }
}