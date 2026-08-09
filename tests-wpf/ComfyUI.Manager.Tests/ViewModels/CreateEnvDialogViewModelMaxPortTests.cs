using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogViewModelMaxPortTests
{
    [Fact]
    public void Ctor_EmptyDb_PortIs8188()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8188", vm.Port);
    }

    [Fact]
    public void Ctor_OneEnvPort8188_PortIs8189()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = 8188,
        });

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8189", vm.Port);
    }

    [Fact]
    public void Ctor_MultipleEnvs_PortIsMaxPlusOne()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-1", "first", 8188));
        repo.Upsert(MakeEnv("env-2", "second", 8200));
        repo.Upsert(MakeEnv("env-3", "third", 8189));

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8201", vm.Port);
    }

    private static Environment MakeEnv(string id, string name, int? port) => new()
    {
        Id = id, Name = name, RootPath = $"/tmp/{name}",
        ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
        PythonVersion = "3.10", Port = port,
    };

    private static Settings MakeSettings() => new() { ActivePythonInterpreterName = "" };
}
