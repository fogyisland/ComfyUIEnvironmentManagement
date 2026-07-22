using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _appDataDir;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeBaseEnvInstaller _installer;

    public BaseEnvViewModelTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
        _installer = new FakeBaseEnvInstaller(_envRepo);
        _appDataDir = Path.Combine(
            Path.GetTempPath(), $"base-env-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDataDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_appDataDir)) Directory.Delete(_appDataDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private BaseEnvViewModel MakeVm()
        => new(new BaseEnvProfileLoader(_appDataDir), _envRepo, _installer);

    private static EnvModel FakeEnv(string id) => new()
    {
        Id = id,
        Name = id,
        RootPath = $"/tmp/{id}",
        VenvPath = $"/tmp/{id}/venv",
        CustomNodesPath = $"/tmp/{id}/nodes",
        Port = 8188,
        Status = "stopped",
    };

    [Fact]
    public void Ctor_DoesNotLoadEnvsOrProfiles()
    {
        var vm = MakeVm();
        Assert.Empty(vm.Profiles);
        Assert.Empty(vm.Envs);
        Assert.Empty(vm.SelectedProfiles);
        Assert.Empty(vm.SelectedEnvIds);
    }

    [Fact]
    public async Task LoadAsync_PopulatesProfilesAndEnvs()
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        _envRepo.Upsert(FakeEnv("env-b"));

        var vm = MakeVm();
        await vm.LoadAsync();

        Assert.Equal(5, vm.Profiles.Count); // 5 默认 profiles
        Assert.Equal(2, vm.Envs.Count);
        Assert.Contains(vm.Envs, e => e.Id == "env-a");
        Assert.Contains(vm.Envs, e => e.Id == "env-b");
    }

    [Fact]
    public async Task LoadAsync_ReloadsEnvsOnSecondCall()
    {
        var vm = MakeVm();
        await vm.LoadAsync();
        Assert.Empty(vm.Envs);

        _envRepo.Upsert(FakeEnv("env-new"));
        _envRepo.Upsert(FakeEnv("env-other"));

        await vm.LoadAsync();

        Assert.Equal(2, vm.Envs.Count);
        Assert.Contains(vm.Envs, e => e.Id == "env-new");
    }

    [Fact]
    public async Task LoadAsync_ClearsPreviousSelection()
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        var vm = MakeVm();
        await vm.LoadAsync();

        vm.SetSelectedProfiles(vm.Profiles.Take(1));
        vm.SetSelectedEnvIds(vm.Envs);
        Assert.Single(vm.SelectedProfiles);
        Assert.Single(vm.SelectedEnvIds);

        await vm.LoadAsync(); // 重新加载应清空选择

        Assert.Empty(vm.SelectedProfiles);
        Assert.Empty(vm.SelectedEnvIds);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SetSelectedProfiles_StoresAndRaisesStartCanExecuteChanged()
    {
        var vm = MakeVm();
        // Need at least one env selection for CanStart to be true.
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.False(vm.StartCommand.CanExecute(null));

        var profiles = new List<BaseEnvProfile>
        {
            new() { Id = "p1", Name = "P1" },
            new() { Id = "p2", Name = "P2" },
        };
        vm.SetSelectedProfiles(profiles);

        Assert.Equal(2, vm.SelectedProfiles.Count);
        Assert.Equal("p1", vm.SelectedProfiles[0].Id);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SetSelectedEnvIds_StoresAndRaisesStartCanExecuteChanged()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a"), FakeEnv("env-b") });

        Assert.Equal(2, vm.SelectedEnvIds.Count);
        Assert.Contains("env-a", vm.SelectedEnvIds);
        Assert.Contains("env-b", vm.SelectedEnvIds);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenNoSelection()
    {
        var vm = MakeVm();
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenEnvsSelectedButNoProfile()
    {
        var vm = MakeVm();
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenProfileSelectedButNoEnv()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CanExecute_WhenBothHaveOne()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_LaunchesBaseEnvProgressDialog_WithFirstProfile()
    {
        var vm = MakeVm();
        env_a_setup(vm);
        var firstProfile = new BaseEnvProfile { Id = "first", Name = "First" };
        var secondProfile = new BaseEnvProfile { Id = "second", Name = "Second" };
        vm.SetSelectedProfiles(new[] { firstProfile, secondProfile });

        (IReadOnlyList<string>? Ids, BaseEnvProfile? Profile, BaseEnvInstaller? Inst) captured =
            (null, null, null);
        vm.ShowDialogOverride = (ids, p, inst) => captured = (ids, p, inst);

        vm.StartCommand.Execute(null);

        Assert.NotNull(captured.Ids);
        Assert.NotNull(captured.Profile);
        Assert.NotNull(captured.Inst);
        Assert.Equal(new[] { "env-a" }, captured.Ids);
        Assert.Same(firstProfile, captured.Profile); // 多 profile → 取第一个
        Assert.Same(_installer, captured.Inst);
    }

    [Fact]
    public void Start_NoOp_WhenNoSelection()
    {
        var vm = MakeVm();
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Start_NoOp_WhenNoProfile()
    {
        var vm = MakeVm();
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Start_NoOp_WhenNoEnv()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(null!, _envRepo, _installer));
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), null!, _installer));
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), _envRepo, null!));
    }

    private void env_a_setup(BaseEnvViewModel vm)
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
    }

    /// <summary>
    /// Minimal local fake:BaseEnvInstallerTests 的 FakeBaseEnvInstaller 是 private nested,
    /// 这里只需要 ctor + InstallAsync 不被实际调用。Start 测试只走 ShowDialogOverride。
    /// </summary>
    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller(EnvironmentRepository envRepo) : base(envRepo) { }
    }
}
