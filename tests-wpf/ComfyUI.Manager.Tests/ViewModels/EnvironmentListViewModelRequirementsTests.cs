using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.5.12: env-list 操作列加 6th 按钮 "装依赖" — 测试 InstallRequirementsCommand 的
/// wiring。RequirementsInstaller 本身的行为在 RequirementsInstallerTests 覆盖。
/// </summary>
public sealed class EnvironmentListViewModelRequirementsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelRequirementsTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(), $"envlistvm-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status = "stopped")
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tempRoot, id),
            ComfyuiLayout = "isolated",
            CustomNodesPath = Path.Combine(_tempRoot, id, "nodes"),
            Status = status,
            BedStatus = "done",
        };
        Directory.CreateDirectory(env.RootPath);
        Directory.CreateDirectory(env.CustomNodesPath);
        _repo.Upsert(env);
        return env;
    }

    private EnvironmentListViewModel NewVm(RequirementsInstaller? installer = null)
    {
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot, installer ?? new RequirementsInstaller());
    }

    [Fact]
    public void InstallRequirementsCommand_EnabledForAnyEnv()
    {
        SeedEnv("env-a");
        var vm = NewVm();
        Assert.True(vm.InstallRequirementsCommand.CanExecute(vm.Environments[0]));
    }

    [Fact]
    public void InstallRequirementsCommand_DisabledWhenNoEnvSelected()
    {
        var vm = NewVm();
        Assert.False(vm.InstallRequirementsCommand.CanExecute(null));
    }

    [Fact]
    public void OpenRequirementsProgress_InvokesShowRequirementsDialogOverride()
    {
        SeedEnv("env-a");
        var vm = NewVm();

        Environment? capturedEnv = null;
        RequirementsInstaller? capturedInstaller = null;
        vm.ShowRequirementsDialogOverride = (env, inst) =>
        {
            capturedEnv = env;
            capturedInstaller = inst;
        };

        vm.InstallRequirementsCommand.Execute(vm.Environments[0]);

        Assert.NotNull(capturedEnv);
        Assert.Equal("env-a", capturedEnv!.Id);
        Assert.NotNull(capturedInstaller);
    }

    [Fact]
    public void InstallRequirementsCommand_RaiseCanExecute_StillExecutable()
    {
        // 验证 RaiseCanExecuteChanged() 不会把 InstallRequirementsCommand 标记 disabled
        SeedEnv("env-a");
        var vm = NewVm();
        vm.InstallRequirementsCommand.RaiseCanExecuteChanged();
        Assert.True(vm.InstallRequirementsCommand.CanExecute(vm.Environments[0]));
    }

    [Fact]
    public void InstallRequirementsCommand_NullEnv_DoesNotInvokeOverride()
    {
        var vm = NewVm();
        bool called = false;
        vm.ShowRequirementsDialogOverride = (_, _) => called = true;

        vm.InstallRequirementsCommand.Execute(null);

        Assert.False(called);  // null 参数 → 命令 predicate 不命中 → CanExecute=false
    }
}
