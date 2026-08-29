using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x:Forge BED inline 状态面板 VM 测试。镜像 RequirementsStatusViewModel
/// test pattern(同样的 Begin / AppendLog / RunAsync / Fail / MarkAlreadyInstalled /
/// Hide 一组)。
/// </summary>
public class BaseEnvStatusViewModelTests : IDisposable
{
    private readonly string _envRoot;

    public BaseEnvStatusViewModelTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"forgebedvm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
        var venvDir = Path.Combine(_envRoot, "venv", "Scripts");
        Directory.CreateDirectory(venvDir);
        File.WriteAllBytes(Path.Combine(venvDir, "python.exe"), new byte[] { 0x00 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string name = "forge")
    {
        return new Environment
        {
            Id = name, Name = name,
            RootPath = _envRoot,
            PythonExecutable = Path.Combine(_envRoot, "venv", "Scripts", "python.exe"),
            TemplateKind = "Forge",
        };
    }

    /// <summary>
    /// 替身 installer:不真跑 pip / git。RunInstaller 控制 success / cancelled / reason。
    /// </summary>
    private class StubInstaller : ForgeBaseEnvInstaller
    {
        public bool RunOk { get; set; }
        public bool RunCancelled { get; set; }
        public string? RunReason { get; set; }

        public StubInstaller() : base() { }

        public override Task<ForgeBedInstallResult> InstallAsync(
            Environment env,
            IProgress<string>? logProgress,
            CancellationToken ct)
        {
            if (RunOk)
            {
                logProgress?.Report("[stub] step 0 torch");
                logProgress?.Report("[stub] step 1 clip");
                logProgress?.Report("[stub] step 5 repos done");
                return Task.FromResult(new ForgeBedInstallResult(
                    Success: true, Cancelled: false, Reason: null, InstalledCount: 0));
            }
            return Task.FromResult(new ForgeBedInstallResult(
                Success: false,
                Cancelled: RunCancelled,
                Reason: RunReason ?? "stub fail",
                InstalledCount: 0));
        }
    }

    [Fact]
    public void Ctor_InitialStatusTextIsPreparingToStart()
    {
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller());
        Assert.Equal("准备开始...", vm.StatusText);
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Empty(vm.LogLines);
    }

    [Fact]
    public void Begin_ResetsToPreparingAndShows()
    {
        // 模拟前面 RunAsync 跑过留下状态,然后再 Begin() 重置
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller { RunOk = false, RunReason = "old fail" });
        // RunAsync 一次让面板进 completed + error 状态
        vm.RunAsync().GetAwaiter().GetResult();

        vm.Begin();

        Assert.Equal("准备开始...", vm.StatusText);
        Assert.True(vm.IsVisible);
        Assert.False(vm.IsComplete);
        Assert.Null(vm.Error);
        Assert.Empty(vm.LogLines);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void MarkAlreadyInstalled_ShowsCompletedWithTimestamp()
    {
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller());

        vm.MarkAlreadyInstalled("2026-08-29T00:00:00Z");

        Assert.True(vm.IsVisible);
        Assert.True(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Contains("已安装 Forge 基础环境", vm.StatusText);
        Assert.Contains("2026-08-29T00:00:00Z", vm.StatusText);
    }

    [Fact]
    public async Task RunAsync_Success_AppendsLogsAndSetsCompleted()
    {
        var env = SeedEnv();
        var installer = new StubInstaller { RunOk = true };
        var vm = new BaseEnvStatusViewModel(env, installer);

        await vm.RunAsync();
        // Progress<T>.Report 通过 SynchronizationContext marshal 回 test thread,
        // callback 可能在 await InstallAsync 之后才 fire。多 yield + 等到 3 条都到位
        // 才 assert。xUnit v2 的 MaxConcurrencySynchronizationContext 是 FIFO,但 Post
        // 是 fire-and-forget,需要反复 yield 让 dispatcher 消费完所有 pending callback。
        var snapshot = new List<string>();
        for (int i = 0; i < 50 && snapshot.Count < 3; i++)
        {
            await Task.Yield();
            snapshot = vm.LogLines.ToList();  // snapshot 避免枚举期间被新 callback 改
        }

        Assert.True(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.Null(vm.Error);
        // LogLines 至少含 stub 上报的 step 行(用 snapshot + null-guarded Contains
        // 而不是 Equal(count) 或直接枚举 vm.LogLines — 后者在 full-suite 跑时偶发
        // (a)少几行 Progress callback 没 fire,或 (b)枚举期间被新 callback 改 collection。
        // 跟 RequirementsStatusViewModelTests 同 tolerant pattern)。
        Assert.NotEmpty(snapshot);
        Assert.Contains(snapshot, l => l != null && l.Contains("step 0 torch"));
        Assert.Contains(snapshot, l => l != null && l.Contains("step 1 clip"));
        Assert.Contains(snapshot, l => l != null && l.Contains("step 5 repos done"));
    }

    [Fact]
    public async Task RunAsync_Cancelled_SetsErrorAndStaysVisible()
    {
        var env = SeedEnv();
        var installer = new StubInstaller { RunCancelled = true };
        var vm = new BaseEnvStatusViewModel(env, installer);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.True(vm.HasError);
        Assert.Equal("用户取消", vm.Error);
        Assert.True(vm.IsVisible);  // 失败 / 取消不自动 Hide,等用户手动关
        Assert.Contains("用户取消", vm.StatusText);
    }

    [Fact]
    public async Task RunAsync_Fail_SetsReasonErrorAndStaysVisible()
    {
        var env = SeedEnv();
        var installer = new StubInstaller { RunOk = false, RunReason = "pip torch 退出码 1" };
        var vm = new BaseEnvStatusViewModel(env, installer);

        await vm.RunAsync();

        Assert.True(vm.IsComplete);
        Assert.True(vm.HasError);
        Assert.Equal("pip torch 退出码 1", vm.Error);
        Assert.True(vm.IsVisible);
        Assert.Contains("pip torch 退出码 1", vm.StatusText);
    }

    [Fact]
    public void Hide_ResetsAllFields()
    {
        // 先 RunAsync 让面板进 completed 状态,然后 Hide 重置
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller { RunOk = true });
        vm.RunAsync().GetAwaiter().GetResult();

        vm.Hide();

        Assert.False(vm.IsVisible);
        Assert.False(vm.IsComplete);
        Assert.Null(vm.Error);
        Assert.Empty(vm.LogLines);
        Assert.Equal("准备开始...", vm.StatusText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void CancelCommand_BeforeRun_IsDisabled()
    {
        // 没 RunAsync 之前 _cts is null → CancelCommand.CanExecute false
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller());
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelCommand_AfterComplete_RaisesCanExecuteChanged()
    {
        // 跟 RequirementsStatusViewModel 同 pattern:RunAsync finally 调
        // RaisePropertyChanged(nameof(CancelCommand)) 让 UI 重新 query CanExecute。
        // _cts 没 null/dispose(下次再点 Cancel 会 cancel 一个已 finished 的 invocation,
        // 但 installer 拿到 ct.IsCancellationRequested=true 立即返 cancel 结果 — 安全)。
        // 这里只验:RunAsync 后 CanExecuteChanged 已被触发(通过 INPC 通知路径)。
        var vm = new BaseEnvStatusViewModel(SeedEnv(), new StubInstaller { RunOk = true });
        var changedCount = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BaseEnvStatusViewModel.CancelCommand))
                changedCount++;
        };

        await vm.RunAsync();

        Assert.True(changedCount >= 1, "CancelCommand 没收到 PropertyChanged 通知");
    }

    [Fact]
    public async Task LogLines_CappedAtMaxLogLines()
    {
        // 镜像 RequirementsStatusViewModel 测试 — 上报 >200 行 → 滚出最早的
        var env = SeedEnv();
        // 造一个 emit 250 行的 stub installer
        var stub = new EmittingInstaller(lineCount: 250);
        var vm = new BaseEnvStatusViewModel(env, stub);

        await vm.RunAsync();

        Assert.Equal(200, vm.LogLines.Count);
        // 最前面若干行(line 0..49)被滚出去了
        Assert.Contains(vm.LogLines, l => l == "[emit] line 250");  // 最后一行应保留
    }

    private class EmittingInstaller : ForgeBaseEnvInstaller
    {
        private readonly int _lineCount;
        public EmittingInstaller(int lineCount) : base() { _lineCount = lineCount; }

        public override Task<ForgeBedInstallResult> InstallAsync(
            Environment env, IProgress<string>? logProgress, CancellationToken ct)
        {
            for (int i = 1; i <= _lineCount; i++)
                logProgress?.Report($"[emit] line {i}");
            return Task.FromResult(new ForgeBedInstallResult(
                Success: true, Cancelled: false, Reason: null, InstalledCount: 0));
        }
    }
}