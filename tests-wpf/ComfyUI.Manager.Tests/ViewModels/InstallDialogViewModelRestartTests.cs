using System;
using System.Collections.Generic;
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

/// <summary>
/// v0.6.11+ SDD D1: InstallDialogViewModel 接受 ctor <c>onInstallSuccess</c> callback,
/// 装成功 (<see cref="NodeOperationResult.Success"/>==true) 时 fire-and-forget
/// 调度到 thread-pool(G7: 不 await,G8: Task.Run,G9: 失败/异常路径不 fire)。
/// </summary>
public class InstallDialogViewModelRestartTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;

    public InstallDialogViewModelRestartTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeRepo = new NodeRepository(_db.Factory);
        SeedEnv("env-1");
    }

    public void Dispose() => _db.Dispose();

    private void SeedEnv(string id)
    {
        _envRepo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = "stopped",
        });
    }

    private static CatalogEntry MakeEntry()
    {
        return new CatalogEntry
        {
            Id = "node-1",
            Package = "ComfyUI-Test",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/test",
            },
        };
    }

    [Fact]
    public async Task Install_Success_FiresOnInstallSuccess_WithEnvId()
    {
        var ops = new StubNodeOperations
        {
            NextResult = NodeOperationResult.Ok("v1.0"),
        };
        var tcs = new TaskCompletionSource<string>();
        var vm = new InstallDialogViewModel(
            _envRepo,
            ops,
            MakeEntry(),
            preselectedEnvId: "env-1",
            onInstallSuccess: async envId => { await Task.Yield(); tcs.SetResult(envId); });

        vm.InstallCommand.Execute(null);

        // Wait for callback (fire-and-forget on thread-pool — TCS completes on it)
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == tcs.Task, "OnInstallSuccess callback was not invoked");
        Assert.Equal("env-1", await tcs.Task);
    }

    [Fact]
    public async Task Install_Failure_DoesNotFireOnInstallSuccess()
    {
        var ops = new StubNodeOperations
        {
            NextResult = NodeOperationResult.Fail("install failed"),
        };
        int callCount = 0;
        var vm = new InstallDialogViewModel(
            _envRepo,
            ops,
            MakeEntry(),
            preselectedEnvId: "env-1",
            onInstallSuccess: _ => { Interlocked.Increment(ref callCount); return Task.CompletedTask; });

        vm.InstallCommand.Execute(null);

        // Wait for InstallAsync to finish (Busy -> false)
        var spin = 0;
        while (vm.Busy && spin < 200)
        {
            await Task.Delay(10);
            spin++;
        }

        // Give the thread-pool a chance to (incorrectly) fire the callback
        await Task.Delay(100);

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Install_Exception_DoesNotFireOnInstallSuccess()
    {
        var ops = new StubNodeOperations
        {
            ThrowOnInstall = new InvalidOperationException("boom"),
        };
        int callCount = 0;
        var vm = new InstallDialogViewModel(
            _envRepo,
            ops,
            MakeEntry(),
            preselectedEnvId: "env-1",
            onInstallSuccess: _ => { Interlocked.Increment(ref callCount); return Task.CompletedTask; });

        vm.InstallCommand.Execute(null);

        // Wait for InstallAsync to finish (Busy -> false)
        var spin = 0;
        while (vm.Busy && spin < 200)
        {
            await Task.Delay(10);
            spin++;
        }

        // Give the thread-pool a chance to (incorrectly) fire the callback
        await Task.Delay(100);

        Assert.Equal(0, callCount);
    }

    private sealed class StubNodeOperations : NodeOperations
    {
        public NodeOperationResult NextResult { get; set; } = NodeOperationResult.Ok("v0");
        public Exception? ThrowOnInstall { get; set; }

        public StubNodeOperations()
            : base(new GitRunner("git"),
                   new EnvironmentRepository(new SqliteConnectionFactory("Data Source=:memory:")),
                   new NodeRepository(new SqliteConnectionFactory("Data Source=:memory:")),
                   new Settings(),
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))))
        { }

        public override Task<NodeOperationResult> InstallAsync(
            string envId, string nodeId, string repoUrl,
            string? targetTag = null,
            IReadOnlyList<PipRequirement>? catalogPipReqs = null,
            CancellationToken ct = default)
        {
            if (ThrowOnInstall is not null) throw ThrowOnInstall;
            return Task.FromResult(NextResult);
        }
    }
}