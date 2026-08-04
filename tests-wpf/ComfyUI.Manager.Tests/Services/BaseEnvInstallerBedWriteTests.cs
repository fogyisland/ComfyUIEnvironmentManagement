using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// BaseEnvInstaller end-state writeback:InstallAsync 末尾逐 env 写 BedProfileId/BedStatus。
/// 用 FakeBaseEnvInstaller 重写 RunPipAsync 控制 exit code / cancel。
/// </summary>
public class BaseEnvInstallerBedWriteTests
{
    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        private readonly Func<string, IReadOnlyList<string>, CancellationToken, PipResult> _handler;
        public FakeBaseEnvInstaller(
            EnvironmentRepository repo,
            Func<string, IReadOnlyList<string>, CancellationToken, PipResult> handler)
            : base(repo)
        {
            _handler = handler;
        }
        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            return Task.FromResult(_handler(pythonExe, pipArgs, ct));
        }
    }

    private static BaseEnvProfile MakeProfile(string id = "pytorch-2.5.0-cu121-stable") =>
        new() { Id = id, Name = id, TorchVersion = "2.5.0", CudaVersion = "cu121" };

    private static Environment SeedEnv(TestDb db, string id, string? venvPythonPath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "bed-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var venv = Path.Combine(root, "venv");
        Directory.CreateDirectory(venv);
        var scripts = Path.Combine(venv, "Scripts");
        Directory.CreateDirectory(scripts);
        var python = Path.Combine(scripts, "python.exe");
        File.WriteAllText(python, "fake");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venv,
            PythonExecutable = venvPythonPath ?? python,
            ComfyuiLayout = "isolated",
            Status = "stopped",
        };
        new EnvironmentRepository(db.Factory).Upsert(env);
        return env;
    }

    [Fact]
    public async Task InstallAsync_OnSuccess_WritesBedStatusDone()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-ok");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(ExitCode: 0, WasCancelled: false));

        var result = await installer.InstallAsync(
            new[] { "env-ok" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-ok");
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("done", fresh.BedStatus);
        Assert.Null(fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_OnPipFailure_WritesBedStatusFailedWithExitCode()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-fail");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(ExitCode: 1, WasCancelled: false));

        await installer.InstallAsync(
            new[] { "env-fail" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-fail");
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("failed", fresh.BedStatus);
        Assert.NotNull(fresh.BedFailedReason);
        Assert.StartsWith("pip 退出码", fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_OnUserCancel_WritesBedStatusFailedWithUserReason()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-cancel");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(ExitCode: -1, WasCancelled: true));

        await installer.InstallAsync(
            new[] { "env-cancel" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-cancel");
        Assert.Equal("failed", fresh!.BedStatus);
        Assert.Equal("用户取消", fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_RerunOverwritesBedStatus()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-rerun");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(ExitCode: 0, WasCancelled: false));

        // 第一次:profile A
        await installer.InstallAsync(
            new[] { "env-rerun" }, MakeProfile("pytorch-2.5.0-cu121-stable"),
            progress: null, ct: default);
        Assert.Equal("pytorch-2.5.0-cu121-stable", repo.Get("env-rerun")!.BedProfileId);

        // 第二次:profile B
        await installer.InstallAsync(
            new[] { "env-rerun" }, MakeProfile("pytorch-nightly-cu126"),
            progress: null, ct: default);
        Assert.Equal("pytorch-nightly-cu126", repo.Get("env-rerun")!.BedProfileId);
        Assert.Equal("done", repo.Get("env-rerun")!.BedStatus);
    }
}
