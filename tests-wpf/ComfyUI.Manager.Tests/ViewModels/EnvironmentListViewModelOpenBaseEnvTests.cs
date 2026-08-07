using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
}