using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelTests
{
    private static void SeedEnv(TestDb db, string id, string status)
    {
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = status,
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
            null!);

        Assert.Equal(2, vm.Environments.Count);
        Assert.Equal("env-1", vm.Environments[0].Id);
    }

    [Fact]
    public void StartCommand_EnabledOnlyForStoppedEnv()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", "stopped");
        SeedEnv(db, "env-2", "running");

        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
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
            profileLoader);

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
            profileLoader);

        IReadOnlyList<string>? capturedEnvIds = null;
        BaseEnvProfile? capturedProfile = null;
        BaseEnvInstaller? capturedInstaller = null;
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
}
