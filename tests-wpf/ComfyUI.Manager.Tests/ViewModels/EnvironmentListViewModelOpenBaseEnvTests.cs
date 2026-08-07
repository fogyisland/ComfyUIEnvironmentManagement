using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelOpenBaseEnvTests
{
    private static EnvironmentListViewModel MakeVm(TestDb db)
    {
        // 用真实 BaseEnvProfileLoader(硬编码 9 个 profile)+ TestDb + null deps。
        // PickerDialogOverride + ShowProgressDialogOverride + MessageBoxOverride 都通过 vm 赋值。
        // ctor 顺序:repo, launcher, envCreator, baseInstaller, settings, profileLoader,
        // envDeleter, nodeOps, projectRoot, requirementsInstaller, baseEnvUninstaller?, requirementsUninstaller?
        // 跟既有 EnvironmentListViewModelUninstallTests.cs:81 一致。
        var profileLoader = new BaseEnvProfileLoader(
            Path.Combine(Path.GetTempPath(), "picker-env-list-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            Path.Combine(Path.GetTempPath(), "picker-env-list-proj-" + Guid.NewGuid()),
            null!);
        return vm;
    }

    private static Environment MakeEnv(string id, string status, string? bedStatus = null) =>
        new()
        {
            Id = id, Name = id, RootPath = $"C:\\envs\\{id}",
            Status = status, BedStatus = bedStatus,
        };

    [Fact]
    public void OpenBaseEnvProgress_EnvAlreadyDone_BailsBeforePicker()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: "done");
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        string? lastMsg = null;
        vm.MessageBoxOverride = msg => lastMsg = msg;
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(pickerCalled);
        Assert.False(launched);
        Assert.NotNull(lastMsg);
        Assert.Contains("已安装", lastMsg!);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerCancel_DoesNotLaunchInstall()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.True(pickerCalled);
        Assert.False(launched);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerReturnsProfile_LaunchesInstall()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        var profile = new BaseEnvProfile { Id = "torch==2.4.1+cu128" };
        vm.PickerDialogOverride = (_, _, _) => new[] { profile };
        BaseEnvProfile? capturedProfile = null;
        vm.ShowProgressDialogOverride = (_, p, _) => capturedProfile = p;
        vm.BaseEnvCommand.Execute(null);
        Assert.NotNull(capturedProfile);
        Assert.Equal("torch==2.4.1+cu128", capturedProfile!.Id);
    }

    [Fact]
    public void OpenBaseEnvProgress_PickerReturnsEmpty_BailsWithMessage()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        vm.PickerDialogOverride = (_, _, _) => Array.Empty<BaseEnvProfile>();
        string? lastMsg = null;
        vm.MessageBoxOverride = msg => lastMsg = msg;
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(launched);
        Assert.NotNull(lastMsg);
        Assert.Contains("请选择", lastMsg!);
    }

    [Fact]
    public void OpenBaseEnvProgress_EnvBusy_BailsBeforePicker()
    {
        using var db = new TestDb();
        var vm = MakeVm(db);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;
        // 模拟 env busy:走反射访问 private _envBusy 字典。
        // 注意:key 是 RootPath 不是 Id(per v0.6.5.22 T4 mutex 设计)。
        // BusyKind 是 private nested enum,这里用 enum 索引值 5(Start)
        // 跟 EnvironmentListViewModel.cs:39 声明顺序对应。
        var busyField = typeof(EnvironmentListViewModel).GetField("_envBusy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = busyField!.GetValue(vm) as System.Collections.IDictionary;
        dict!.Add(env.RootPath, 5);  // BusyKind.Start
        var pickerCalled = false;
        vm.PickerDialogOverride = (_, _, _) => { pickerCalled = true; return null; };
        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;
        vm.BaseEnvCommand.Execute(null);
        Assert.False(pickerCalled);
        Assert.False(launched);
    }

    // v0.6.6.1 hotfix:env-list 工具栏 BED 入口改用 LoadAsync() 而不是 GetHardcodedDefaults(),
    // 跟 BaseEnvView tab 同源 — 让 env-list picker 也能选全版本(之前 sync hardcoded 只暴露
    // torch 2.4.1 + nightly)。
    [Fact]
    public void OpenBaseEnvProgress_PassesProfilesFromLoadAsync_NotHardcodedDefaults()
    {
        using var db = new TestDb();
        // 用 TestBaseEnvProfileLoader 喂一个明确的 multi-version profile list(torch 2.4.1 +
        // 2.5.1 + nightly),LoadAsync 直接返回这个 list(不调 hardcoded),GetHardcodedDefaults 也覆盖
        // — 如果 vm 错误地走 GetHardcodedDefaults(),picker 看到的 TorchVersion 只会有 "2.4.1"
        // 和 "nightly"(无 2.5.1),测试断言会失败。
        var customProfiles = new List<BaseEnvProfile>
        {
            new() { Id = "pytorch-2.4.1-cu118-stable", Name = "2.4.1+cu118", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
            new() { Id = "pytorch-2.5.1-cu121-stable", Name = "2.5.1+cu121", TorchVersion = "2.5.1", CudaVersion = "cu121", Channel = "stable" },
            new() { Id = "pytorch-2.6.0-cu126-stable", Name = "2.6.0+cu126", TorchVersion = "2.6.0", CudaVersion = "cu126", Channel = "stable" },
            new() { Id = "pytorch-nightly-cu126", Name = "nightly+cu126", TorchVersion = "nightly", CudaVersion = "cu126", Channel = "nightly" },
        };
        var appDataDir = Path.Combine(Path.GetTempPath(), "picker-env-list-v0661-" + Guid.NewGuid());
        var profileLoader = new TestLoadAsyncProfileLoader(appDataDir, customProfiles);
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            Path.Combine(Path.GetTempPath(), "picker-env-list-proj-" + Guid.NewGuid()),
            null!);
        var env = MakeEnv("e1", "stopped", bedStatus: null);
        new EnvironmentRepository(db.Factory).Upsert(env);
        vm.Selected = env;

        IReadOnlyList<BaseEnvProfile>? capturedProfiles = null;
        vm.PickerDialogOverride = (profiles, _, _) =>
        {
            capturedProfiles = profiles.ToList();
            return profiles.Take(1).ToArray();  // 选第一项让流程走完
        };
        bool installLaunched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => installLaunched = true;

        // Execute(null) fire-and-forget 触发 async lambda,但测试 fixture 中 LoadAsync 是同步回
        // 完成的 completed Task,所以 picker 在 Execute 返回前已调用 — 注意 race。
        vm.BaseEnvCommand.Execute(null);

        Assert.NotNull(capturedProfiles);
        var versions = capturedProfiles!.Select(p => p.TorchVersion).Distinct().OrderBy(v => v).ToList();
        // LoadAsync 返回 4 个不同 torch 版本(2.4.1 / 2.5.1 / 2.6.0 / nightly)。
        Assert.Equal(4, capturedProfiles!.Count);
        Assert.Equal(new[] { "2.4.1", "2.5.1", "2.6.0", "nightly" }, versions);
        Assert.True(installLaunched);  // 流程确实走到了 install 阶段(确认 picker 完整走完)
    }

    private sealed class TestLoadAsyncProfileLoader : BaseEnvProfileLoader
    {
        private readonly IReadOnlyList<BaseEnvProfile> _profiles;
        public TestLoadAsyncProfileLoader(string appDataDir, IReadOnlyList<BaseEnvProfile> profiles)
            : base(appDataDir) { _profiles = profiles; }
        public override Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(_profiles);
        // GetHardcodedDefaults 故意返回 *不同* 的 list — 测试如果走到这里(case 走到错误分支)
        // 会发现 torch 列表不匹配。如果 vm 错误地用 GetHardcodedDefaults,捕获的 profiles 会
        // 是这个 empty list 而不是 _profiles。
        public override IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults() => Array.Empty<BaseEnvProfile>();
    }
}