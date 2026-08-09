using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public class NodeInstallDiffServiceTests
{
    [Fact]
    public async Task CheckAsync_NewPackage_NotInWarnings()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("numpy", ">=1.24") },
            default);

        Assert.Empty(report.Warnings);
        Assert.Single(report.Entries);
        Assert.Equal(DiffCategory.New, report.Entries[0].Category);
    }

    [Fact]
    public async Task CheckAsync_Upgrade_NotInWarnings()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"1.0.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", ">=2.0") },
            default);

        Assert.Empty(report.Warnings);
        Assert.Single(report.Entries);
        Assert.Equal(DiffCategory.Upgrade, report.Entries[0].Category);
    }

    [Fact]
    public async Task CheckAsync_Downgrade_AddedToWarnings()
    {
        // env has torch 2.5.0, node spec wants <=1.5 → install will downgrade
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", "<=1.5") },
            default);

        Assert.Single(report.Warnings);
        Assert.Equal("torch", report.Warnings[0].Name);
        Assert.Equal(DiffCategory.Downgrade, report.Warnings[0].Category);
        Assert.Equal("2.5.0", report.Warnings[0].FromVersion);
    }

    [Fact]
    public async Task CheckAsync_Conflict_AddedToWarnings()
    {
        // env has torch 2.5.0, node spec wants <1 → conflict (no overlap)
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", "<1") },
            default);

        Assert.Single(report.Warnings);
        Assert.Equal(DiffCategory.Conflict, report.Warnings[0].Category);
    }

    [Fact]
    public async Task CheckAsync_EmptyCatalogReqs_EmptyReport()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(true, 0,
            """[{"name":"torch","version":"2.5.0"}]""", ""));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            Array.Empty<PipRequirement>(),
            default);

        Assert.Empty(report.Entries);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task CheckAsync_PipListFails_EmptyReport_NoThrow()
    {
        var fakeRunner = new FakeProcessRunner(new ProcessResult(false, 1, "", "ERROR: no pip"));
        var svc = new NodeInstallDiffService(fakeRunner.RunAsync);

        var report = await svc.CheckAsync(
            MakeEnv("/usr/bin/python"),
            new[] { new PipRequirement("torch", ">=2.0") },
            default);

        Assert.Empty(report.Entries);
        Assert.Empty(report.Warnings);
    }

    private static Environment MakeEnv(string pythonExe) => new()
    {
        Id = "env-1",
        Name = "test",
        RootPath = "/tmp/test",
        ComfyuiLayout = "shared",
        BasePythonPath = pythonExe,
        PythonVersion = "3.10",
        PythonExecutable = pythonExe,
    };

    private sealed class FakeProcessRunner
    {
        private readonly ProcessResult _result;
        public FakeProcessRunner(ProcessResult result) => _result = result;
        public Task<ProcessResult> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(_result);
    }
}