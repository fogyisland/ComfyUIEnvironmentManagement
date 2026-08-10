using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelTests
{
    private static void SeedEnv(TestDb db, string id, string status, string? bedStatus = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = status,
            BedStatus = bedStatus,
        });
    }

    [Fact]
    public void Load_PopulatesEnvironmentsFromRepository()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped");
        SeedEnv(db, "env-2", "running");

        // Launcher is not exercised by these VM tests; pass null! so the
        // VM can be constructed without bringing up real processes.
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.Equal(2, vm.Environments.Count);
        Assert.Equal("env-1", vm.Environments[0].Id);
    }

    [Fact]
    public void StartCommand_EnabledOnlyForStoppedEnv()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped", "done");
        SeedEnv(db, "env-2", "running");

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.True(vm.StartCommand.CanExecute(vm.Environments[0]));
        Assert.False(vm.StartCommand.CanExecute(vm.Environments[1]));
        Assert.False(vm.StopCommand.CanExecute(vm.Environments[0]));
        Assert.True(vm.StopCommand.CanExecute(vm.Environments[1]));
    }

    [Fact]
    public void RefreshCommand_ReloadsFromRepository()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped");

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        Assert.Single(vm.Environments);

        SeedEnv(db, "env-2", "stopped");
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Environments.Count);
    }

    [Fact]
    public void BaseEnvCommand_DisabledWhenNoEnvs()
    {
        using var db = new TestDb();
        // No envs seeded.

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.False(vm.BaseEnvCommand.CanExecute(null));
    }

    [Fact]
    public void BaseEnvCommand_EnabledWhenEnvsExist()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped");

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.True(vm.BaseEnvCommand.CanExecute(null));
    }

    [Fact]
    public void OpenBaseEnvProgress_NoEnvs_NoDialogLaunched()
    {
        using var db = new TestDb();

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "empty-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            profileLoader,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var launched = false;
        vm.ShowProgressDialogOverride = (_, _, _) => launched = true;

        vm.BaseEnvCommand.Execute(null);

        Assert.False(launched);
    }

    [Fact]
    public void OpenBaseEnvProgress_WithEnv_LaunchesDialogWithEnvIdAndDefaultProfile()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped");

        var profileLoader = new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            profileLoader,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        IReadOnlyList<string>? capturedEnvIds = null;
        BaseEnvProfile? capturedProfile = null;
        BaseEnvInstaller? capturedInstaller = null;
        // v0.6.6:env-list 工具栏入口接 picker,需要设 PickerDialogOverride 选第一个 profile
        var firstProfile = profileLoader.GetHardcodedDefaults()[0];
        vm.PickerDialogOverride = (_, _, _) => new[] { firstProfile };
        vm.ShowProgressDialogOverride = (ids, p, i) =>
        {
            capturedEnvIds = ids;
            capturedProfile = p;
            capturedInstaller = i;
        };

        vm.BaseEnvCommand.Execute(null);

        Assert.NotNull(capturedEnvIds);
        Assert.Single(capturedEnvIds);
        Assert.Equal("env-1", capturedEnvIds![0]);
        Assert.NotNull(capturedProfile);
        // Default profile's first item should be cu118 stable (per T2's GetDefaults() ordering).
        Assert.Equal("cu118", capturedProfile!.CudaVersion);
        Assert.Null(capturedInstaller);  // We passed null! in ctor.
    }

    [Fact]
    public void RecentBasePythonPath_NullWhenListEmpty()
    {
        using var db = new TestDb();
        // No envs seeded — Environments should be empty and RecentBasePythonPath null.

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.Null(vm.RecentBasePythonPath);
    }

    [Fact]
    public void RecentBasePythonPath_LastCreatedEnvBasePython()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var env1 = new Environment
        {
            Id = "env-a",
            Name = "alpha",
            RootPath = @"C:\envs\env-a",
            ComfyuiLayout = "isolated",
            Status = "stopped",
            BasePythonPath = "/tmp/a.exe",
        };
        var env2 = new Environment
        {
            Id = "env-b",
            Name = "beta",
            RootPath = @"C:\envs\env-b",
            ComfyuiLayout = "isolated",
            Status = "stopped",
            BasePythonPath = "/tmp/b.exe",
        };
        repo.Upsert(env1);
        repo.Upsert(env2);

        // The seeded RootPaths do not exist on the test machine, so Directory.Exists
        // returns false for both and the mtime key falls back to 0 for both. The
        // secondary sort key is Id descending — "env-b" > "env-a", so env2 wins.
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Assert.Equal("/tmp/b.exe", vm.RecentBasePythonPath);
    }

    [Fact]
    public async Task DeleteCommand_CallsDeleterWhenConfirmed_AndReloads()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-del", "stopped");

        var repo = new EnvironmentRepository(db.Factory);
        var processStateRepo = new ProcessStateRepository(db.Factory);
        var launcher = new ProcessLauncher(Path.GetTempPath(), db.Factory, repo, processStateRepo);
        var deleter = new EnvDeleterService(repo, launcher);

        var vm = new EnvironmentListViewModel(
            repo,
            launcher,
            null!,
            null!,
            null!,
            null!,
            deleter,
            null!,
            Path.GetTempPath(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!)
        {
            ConfirmDeleteOverride = _ => true,
        };

        vm.Selected = vm.Environments[0];
        vm.DeleteCommand.Execute(vm.Environments[0]);

        // deleter 真删了 → repo 空 → Load 重读后 vm 也空
        await Task.Delay(50);
        Assert.Empty(vm.Environments);
    }

    [Fact]
    public async Task DeleteCommand_DoesNothingWhenCancelled()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-keep", "stopped");

        var repo = new EnvironmentRepository(db.Factory);
        var processStateRepo = new ProcessStateRepository(db.Factory);
        var launcher = new ProcessLauncher(Path.GetTempPath(), db.Factory, repo, processStateRepo);
        var deleter = new EnvDeleterService(repo, launcher);

        var vm = new EnvironmentListViewModel(
            repo,
            launcher,
            null!,
            null!,
            null!,
            null!,
            deleter,
            null!,
            Path.GetTempPath(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!)
        {
            ConfirmDeleteOverride = _ => false,
        };

        vm.Selected = vm.Environments[0];
        vm.DeleteCommand.Execute(vm.Environments[0]);

        await Task.Delay(50);
        Assert.Single(vm.Environments);
    }

    [Fact]
    public void InstallNodeCommand_PassesBoundEnvToPickerOverride()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-x", "stopped");

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            Path.GetTempPath(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        Environment? captured = null;
        vm.OpenInstallPickerOverride = env => captured = env;

        vm.InstallNodeCommand.Execute(vm.Environments[0]);

        Assert.NotNull(captured);
        Assert.Equal("env-x", captured!.Id);
    }

    /// <summary>
    /// v0.6.11+ T1 (G4):删 BaseEnvViewModel 后,MarkIncompatibleOlderVersions
    /// (torch&lt;2.4 profile "不推荐" suffix)必须继续生效。<see cref="BaseEnvProfileLoader.LoadAsync"/>
    /// 内部在返回前过 <c>MarkIncompatibleOlderVersions</c>——所以验证 G4 invariant 等价于验证
    /// <see cref="EnvironmentListViewModel"/> 触发 BED 流程时调了 1 次
    /// <c>BaseEnvProfileLoader.LoadAsync</c>(静态方法 transitively 跑)。
    /// </summary>
    [Fact]
    public async Task BaseEnvCommand_InvokesProfileLoaderLoadAsync()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-bed", "stopped");

        var fakeLoader = new CountingProfileLoader();
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            fakeLoader,
            null!,
            null!,
            Path.GetTempPath(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        // 拦截 picker dialog,选第 1 个 profile 即可,不弹真 dialog。
        var firstProfile = fakeLoader.GetHardcodedDefaults()[0];
        vm.PickerDialogOverride = (_, _, _) => new[] { firstProfile };
        vm.ShowProgressDialogOverride = (_, _, _) => { };

        vm.BaseEnvCommand.Execute(null);

        // 等 fire-and-forget 走完。
        await Task.Delay(100);

        Assert.Equal(1, fakeLoader.LoadAsyncCallCount);
    }

    /// <summary>
    /// v0.6.11+ T1 (G4):验证 MarkIncompatibleOlderVersions 静态方法对旧 torch
    /// 版本(profile.Name="PyTorch 2.1.0 + CUDA 11.8 (stable)")加 "不推荐" 后缀。
    /// 等价于集成测试 G4 invariant:静态方法在 BaseEnvProfileLoader 内部正常跑,
    /// torch&lt;2.4 profile 在 UI 显 "不推荐"。
    /// </summary>
    [Fact]
    public void MarkIncompatibleOlderVersions_OldTorchVersion_AppendsSuffix()
    {
        var profiles = new List<BaseEnvProfile>
        {
            new()
            {
                Id = "pytorch-2.1.0-cu118-stable",
                Name = "PyTorch 2.1.0 + CUDA 11.8 (stable)",
                TorchVersion = "2.1.0",
                CudaVersion = "cu118",
                Channel = "stable",
                Packages = new List<string> { "torch" },
            },
        };
        var marked = BaseEnvProfileLoader.MarkIncompatibleOlderVersions(profiles);
        Assert.EndsWith(" (不推荐 — comfy_kitchen 不兼容)", marked[0].Name);
        Assert.Equal("pytorch-2.1.0-cu118-stable", marked[0].Id);  // Id 不动(v0.6.5.22 fix)
    }

    /// <summary>
    /// v0.6.11+ T1 (G4 backward compat):EnvListVM 接受 profileLoader 时,既有
    /// null! 测试构造 pattern 仍能编译运行(profileLoader 参数非空约束被现有 ctor 强制)。
    /// 验证改 ctor 不破坏既有测试构造语义。
    /// </summary>
    [Fact]
    public void ExistingNullProfileLoaderCtor_StillCompilesAndConstructs()
    {
        using var db = new TestDb();
        // 既有 pattern:profileLoader 传 null!(模拟 ctor 5th 参数)。
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            Path.GetTempPath(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        Assert.NotNull(vm);
    }

    /// <summary>
    /// v0.6.11+ T1 测试 fake:计 <see cref="BaseEnvProfileLoader.LoadAsync"/> 调用次数。
    /// 跟既有的 <c>TestLoadAsyncProfileLoader</c>(OpenBaseEnvTests)同 pattern,只是加
    /// CallCount field 验 BaseEnvCommand 触发 1 次。
    /// </summary>
    private sealed class CountingProfileLoader : BaseEnvProfileLoader
    {
        public int LoadAsyncCallCount { get; private set; }

        public CountingProfileLoader()
            : base(Path.Combine(Path.GetTempPath(), "bed-counting-" + Guid.NewGuid()))
        {
        }

        public override Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
        {
            LoadAsyncCallCount++;
            return base.LoadAsync(ct);
        }
    }
}
