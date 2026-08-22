using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainViewModelTemplateUpdateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public MainViewModelTemplateUpdateTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "main-vm-template-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private MainViewModel NewMainVm(ComfyUITemplateUpdater? templateUpdater = null)
    {
        var svc = new UiPreferencesService(_projectRoot);
        var main = new MainViewModel(
            _db.Factory, null!, null!, null!, null!, null!, null!, null!,
            new Settings(), null!, null!, null!, null!, null!, null!,
            null!, "", _projectRoot, null!, null!, svc,
            templateUpdater: templateUpdater);
        main.EnvironmentsViewFactory = vm => new object();   // 避 STA
        return main;
    }

    [Fact]
    public void UpdateTemplateCommand_CanExecute_FalseWhenUpdaterNotInjected()
    {
        // v0.6.22.x:模板更新 command 必须 _templateUpdater 已注入才能 execute —
        // 没有 service = 永远 disabled,跟 v0.6.22 T5 旧 EnvListVM 行为一致。
        var main = NewMainVm(templateUpdater: null);
        Assert.False(main.UpdateTemplateCommand.CanExecute(null));
    }

    [Fact]
    public void UpdateTemplateCommand_CanExecute_TrueWhenUpdaterInjected()
    {
        // v0.6.22.x:有 service + !IsBusy = CanExecute true,菜单项可点。
        var updater = new ComfyUITemplateUpdater(new GitRunner("git"));
        var main = NewMainVm(templateUpdater: updater);
        Assert.True(main.UpdateTemplateCommand.CanExecute(null));
    }

    [Fact]
    public void UpdateTemplateCommand_RunsUpdaterOnTargetDir_AfterConfirmAccepted()
    {
        // v0.6.22.x:模板更新执行 → confirm dialog 通过 → updater 收到正确 targetDir
        // (= <projectRoot>/ComfyUI/)。测试 seam ConfirmDangerousOverride 替 MessageBox。
        var updater = new ComfyUITemplateUpdater(new GitRunner("git"));
        var main = NewMainVm(templateUpdater: updater);

        string? capturedTargetDir = null;
        // 拦截 updater:不真跑 wipe+clone,只记录传入的 targetDir
        var capturingUpdater = new CapturingTemplateUpdater(new GitRunner("git"))
        {
            OnUpdate = dir => capturedTargetDir = dir,
        };
        var mainWithCapture = NewMainVm(templateUpdater: capturingUpdater);

        mainWithCapture.ConfirmDangerousOverride = (msg, title) => true;   // 接受确认
        mainWithCapture.UpdateTemplateCommand.Execute(null);

        // 因为是 fire-and-forget (Task.Run) 派发,等任务结束
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (capturedTargetDir is null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(50);
        }

        Assert.NotNull(capturedTargetDir);
        Assert.Equal(Path.Combine(_projectRoot, "ComfyUITemplate"), capturedTargetDir);
    }

    [Fact]
    public void UpdateTemplateCommand_NoOp_WhenConfirmRejected()
    {
        // v0.6.22.x:用户拒绝 confirm → updater 永远不会被调用,避免误操作。
        var capturingUpdater = new CapturingTemplateUpdater(new GitRunner("git"));
        var main = NewMainVm(templateUpdater: capturingUpdater);

        main.ConfirmDangerousOverride = (msg, title) => false;   // 拒绝
        main.UpdateTemplateCommand.Execute(null);

        System.Threading.Thread.Sleep(200);   // 等异步 fire-and-forget 跑完
        Assert.Equal(0, capturingUpdater.CallCount);
    }

    /// <summary>v0.6.22.x:test fake,替 ComfyUITemplateUpdater 拦截 UpdateAsync 调用,
    /// 记录参数 + 调用次数(避免真跑 wipe + git clone)。</summary>
    private sealed class CapturingTemplateUpdater : ComfyUITemplateUpdater
    {
        public Action<string>? OnUpdate { get; set; }
        public int CallCount { get; private set; }

        public CapturingTemplateUpdater(GitRunner git) : base(git) { }

        public override Task<NodeOperationResult> UpdateAsync(
            string targetDir, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            OnUpdate?.Invoke(targetDir);
            // 立刻 cancel 阻止 git clone(只在 confirm accepted 后会到这一步)
            return Task.FromResult(NodeOperationResult.Ok(null));
        }
    }
}