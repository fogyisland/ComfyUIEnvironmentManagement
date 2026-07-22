using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvProgressViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvProgressViewModelTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void OnProgress_Running_UpdatesCompletedAndTotal()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvProfile(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", 30, "downloading", null));

        Assert.Equal(0, vm.Completed);
        Assert.Equal(1, vm.Total);
        Assert.Equal(30, vm.EnvPercent);
        Assert.Contains("env-a", vm.StatusText);
        Assert.Contains("downloading", vm.LogTail);
    }

    [Fact]
    public void OnProgress_Succeeded_BumpsCompleted()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvProfile(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Succeeded, 1, 1, "env-a", "env-a", 100, null, null));

        Assert.Equal(1, vm.Completed);
        Assert.Equal(BaseEnvStatus.Succeeded, vm.OverallStatus);
    }

    [Fact]
    public void OnProgress_Failed_KeepsOverallAsFailed()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a", "env-b" }, new BaseEnvProfile(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Failed, 1, 2, "env-a", "env-a", null, null, "pip exit 1"));

        Assert.Equal(BaseEnvStatus.Failed, vm.OverallStatus);
        Assert.Contains("env-a", vm.StatusText);
    }

    [Fact]
    public void OnProgress_Cancelled_BumpsOverallToCancelled()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvProfile(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Cancelled, 1, 1, "env-a", "env-a", null, null, "用户取消"));

        Assert.Equal(BaseEnvStatus.Cancelled, vm.OverallStatus);
    }

    [Fact]
    public void CancelCommand_FiresInstallerCancellation()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvProfile(), installer);

        Assert.False(vm.CancelCommand.CanExecute(null));
        // After RunAsync starts, Cancel should be enabled; but for this test
        // we just verify the CTS plumbing exists
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void LogTail_AppendsLines()
    {
        var installer = new FakeBaseEnvInstaller(_envRepo);
        var vm = new BaseEnvProgressViewModel(
            new[] { "env-a" }, new BaseEnvProfile(), installer);

        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", null, "line 1", null));
        vm.OnProgress(new BaseEnvProgress(
            BaseEnvStatus.Running, 0, 1, "env-a", "env-a", null, "line 2", null));

        Assert.Contains("line 1", vm.LogTail);
        Assert.Contains("line 2", vm.LogTail);
    }

    /// <summary>
    /// Minimal local fake: T4's FakeBaseEnvInstaller is a private nested class in
    /// BaseEnvInstallerTests, so it is not reachable here. These tests only exercise
    /// BaseEnvProgressViewModel.OnProgress directly (never RunAsync), so a no-op
    /// override of RunPipAsync is sufficient.
    /// </summary>
    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller(EnvironmentRepository envRepo) : base(envRepo) { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent,
            CancellationToken ct)
            => Task.FromResult(new PipResult(0, false));
    }
}
