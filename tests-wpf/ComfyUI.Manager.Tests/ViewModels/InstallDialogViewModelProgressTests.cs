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
/// v0.6.15.5 T3: InstallDialogViewModel 接 IProgress&lt;string&gt; → ProgressPercent +
/// ProgressLog + CancelCommand。
/// <para>
/// 适配 codebase (跟 brief 有偏离):
/// - 没有 SqliteConnectionFactory.InitializeForTests — 用 TestDb
/// - 没有 TestEnvFactory — inline Environment 构造(跟 InstallDialogViewModelRestartTests 同款)
/// - EnvironmentRepository ctor 要 SqliteConnectionFactory — 用 _db.Factory
/// - NodeInstallDiffService ctor = (runProcess, logger?) — noop service
/// - CatalogEntry.Id 是 string 不是 Guid
/// </para>
/// </summary>
public class InstallDialogViewModelProgressTests : IDisposable
{
    private readonly TestDb _db;
    private readonly EnvironmentRepository _envRepo;

    public InstallDialogViewModelProgressTests()
    {
        _db = new TestDb();
        _envRepo = new EnvironmentRepository(_db.Factory);
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
            Package = "test-node",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/test",
            },
        };
    }

    [Fact]
    public async Task InstallAsync_GitProgress_UpdatesProgressPercent()
    {
        var ops = new FakeNodeOperations();
        ops.ProgressToReport = new[] { "Receiving objects:  45%", "Receiving objects: 100%" };
        var vm = new InstallDialogViewModel(_envRepo, ops, MakeEntry(), preselectedEnvId: "env-1");

        vm.InstallCommand.Execute(null);

        // 等 InstallAsync 整个跑完 + 所有 Progress<T> callback 都执行。
        // busy-wait 看 ProgressPercent:在所有 Report 都处理完时 = 100,
        // 然后 success 分支不会再覆盖 ProgressPercent(regex match 仍 100)。
        // 这样我们拿到的 Percent 一定是最后一个 Report 的值。
        var spin = 0;
        while (vm.Busy && spin < 500)
        {
            await Task.Delay(10);
            spin++;
        }
        // Progress<T> 是 async dispatch,等 200ms 让 callback 跑完
        await Task.Delay(200);

        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(2, vm.ProgressLog.Count);
        Assert.Equal("Receiving objects: 100%", vm.ProgressLog[1]);
    }

    [Fact]
    public async Task InstallAsync_GitProgress_AppendsToProgressLog()
    {
        var ops = new FakeNodeOperations();
        ops.ProgressToReport = new[] { "Receiving objects:  45%", "Resolving deltas: 100%" };
        var vm = new InstallDialogViewModel(_envRepo, ops, MakeEntry(), preselectedEnvId: "env-1");

        vm.InstallCommand.Execute(null);

        var spin = 0;
        while ((vm.Busy || vm.ProgressLog.Count < 2) && spin < 500)
        {
            await Task.Delay(10);
            spin++;
        }

        Assert.Equal(2, vm.ProgressLog.Count);
        Assert.Equal("Receiving objects:  45%", vm.ProgressLog[0]);
        Assert.Equal("Resolving deltas: 100%", vm.ProgressLog[1]);
    }

    [Fact]
    public async Task InstallAsync_Regex_OnlyWholeNumberPercent()
    {
        var ops = new FakeNodeOperations();
        // 没有百分号的行 — regex 不 match,ProgressPercent 保持 0
        ops.ProgressToReport = new[] { "Receiving objects: 1234/5678" };
        var vm = new InstallDialogViewModel(_envRepo, ops, MakeEntry(), preselectedEnvId: "env-1");

        vm.InstallCommand.Execute(null);

        var spin = 0;
        while (vm.Busy && spin < 500)
        {
            await Task.Delay(10);
            spin++;
        }
        // 等 Progress<T> callback 把 1 行写进 log
        await Task.Delay(200);

        Assert.Equal(0, vm.ProgressPercent);
        Assert.Equal("Receiving objects: 1234/5678", vm.ProgressLog[0]);
    }

    [Fact]
    public async Task CancelCommand_TriggersCancellation()
    {
        var ops = new FakeNodeOperations();
        ops.BlockUntilCancelled = true;
        ops.ProgressToReport = Array.Empty<string>();
        var vm = new InstallDialogViewModel(_envRepo, ops, MakeEntry(), preselectedEnvId: "env-1");

        // 后台线程跑 InstallCommand (RelayCommand.Execute 是 sync 触发 async,但
        // 我们要让 install 在跑时还能去点 CancelCommand — 多线程模拟)
        var installTask = Task.Run(() => vm.InstallCommand.Execute(null));

        // 等 install 真正开始 (Busy=true)
        var spin = 0;
        while (!vm.Busy && spin < 200)
        {
            await Task.Delay(10);
            spin++;
        }

        vm.CancelCommand.Execute(null);

        // 等 install 跑完 (catch block → Progress="用户取消" → Busy=false)
        await installTask;
        var wait = 0;
        while (vm.Busy && wait < 200)
        {
            await Task.Delay(10);
            wait++;
        }

        Assert.True(ops.CancelCalled);
        Assert.Equal("用户取消", vm.Progress);
    }
}

/// <summary>
/// Test fake: 替代 NodeOperations,提供可控的 IProgress&lt;string&gt; 输出 + 模拟取消阻塞。
/// <para>
/// v0.6.15.5 T2 把 InstallAsync 改成 virtual + 加 IProgress&lt;string&gt;? progress 与
/// CancellationToken ct 两个新参数,所以这里 override 接住它们。
/// </para>
/// </summary>
internal class FakeNodeOperations : NodeOperations
{
    public string[] ProgressToReport { get; set; } = Array.Empty<string>();
    public bool BlockUntilCancelled { get; set; }
    public bool CancelCalled { get; private set; }

    public FakeNodeOperations()
        : base(new GitRunner("git"),
               new EnvironmentRepository(new SqliteConnectionFactory("Data Source=:memory:")),
               new NodeRepository(new SqliteConnectionFactory("Data Source=:memory:")),
               new Settings(),
               new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))))
    { }

    public override async Task<NodeOperationResult> InstallAsync(
        string envId, string nodeId, string repoUrl,
        string? targetTag = null,
        IReadOnlyList<PipRequirement>? catalogPipReqs = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (progress is not null)
        {
            // 跟生产代码保真度:GitRunner 是真异步,会让出控制权。fake 模拟用
            // Task.Run + 短暂 sleep 让 ThreadPool 把 Progress<T> 派发的 lambda
            // 真正执行完。否则 InstallAsync continuation 在 success 分支
            // 覆盖 vm.Progress = "OK,...",race 让测试断言不稳定。
            foreach (var line in ProgressToReport)
            {
                progress.Report(line);
                await Task.Delay(30); // 给 ThreadPool 时间 dispatch 上面 Report 的 lambda
            }
            // 最终 delay 确保最后一个 Report 完全处理完
            await Task.Delay(100);
        }
        if (BlockUntilCancelled)
        {
            // 阻塞直到 ct 被取消(由 CancelCommand → _cts.Cancel() 触发)
            try
            {
                ct.WaitHandle.WaitOne();
                CancelCalled = true;
            }
            catch (Exception) { CancelCalled = true; }
            throw new OperationCanceledException(ct);
        }
        return NodeOperationResult.Ok("sha-fake");
    }
}
