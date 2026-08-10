using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class EnvironmentListViewModelComfyUiManagerTests
{
    [Fact]
    public void Load_PopulatesIsComfyUiManagerInstalledFalse_WhenManagerDirMissing()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        Assert.Single(sut.Environments);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public void Load_PopulatesIsComfyUiManagerInstalledTrue_WhenManagerDirExists()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        var comfyuiSource = Path.Combine(env.RootPath, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var sut = MakeSut(db);

        Assert.True(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("卸载 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public void ToggleComfyUiManagerCommand_DisabledWhenBusy()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        // Simulate busy via ToggleComfyUiManagerAsync internal mutex (use a public seam)
        sut.SetComfyUiManagerBusyForTest(sut.Environments[0]);
        Assert.False(sut.ToggleComfyUiManagerCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public void ToggleComfyUiManagerCommand_EnabledWhenIdle_NotInstalled()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        Assert.True(sut.ToggleComfyUiManagerCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_NotInstalled_TriggersInstall()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var fakeInstaller = new FakeComfyUIManagerInstaller { NextResult = NodeOperationResult.Ok("1") };
        var sut = MakeSut(db, fakeInstaller);

        var task = sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        await task;
        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.True(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("卸载 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_Installed_TriggersUninstall()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        var comfyuiSource = Path.Combine(env.RootPath, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var fakeInstaller = new FakeComfyUIManagerInstaller { NextResult = NodeOperationResult.Ok(null) };
        var sut = MakeSut(db, fakeInstaller);

        var task = sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        await task;
        Assert.Equal(1, fakeInstaller.UninstallCallCount);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
    }

    [Fact]
    public async Task ToggleComfyUiManagerAsync_InstallFails_LeavesButtonAsInstall()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var fakeInstaller = new FakeComfyUIManagerInstaller
        {
            NextResult = NodeOperationResult.Fail("git clone 失败"),
        };
        var sut = MakeSut(db, fakeInstaller);

        await sut.ToggleComfyUiManagerAsync(sut.Environments[0]);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.False(sut.Environments[0].IsComfyUiManagerInstalled);
        Assert.Equal("安装 ComfyUI Manager", sut.Environments[0].ComfyUiManagerButtonText);
        Assert.True(sut.ComfyUiManagerStatus?.HasError);
    }

    private static Environment SeedEnv(TestDb db, string id)
    {
        // 用 GUID 后缀的临时 root,避免多测试间 C:\envs\env-1 目录残留污染
        // ComfyUI-Manager 装态测试(IsInstalled 检查文件系统)。
        var root = Path.Combine(
            Path.GetTempPath(),
            $"comfyui-manager-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = id, Name = id,
            RootPath = root,
            // ComfyuiSource 设为 <root>/ComfyUI(独立 isolated 模式),跟
            // Load_PopulatesIsComfyUiManagerInstalledTrue_WhenManagerDirExists
            // 创建的 <root>/ComfyUI/custom_nodes/ComfyUI-Manager 路径匹配。
            ComfyuiSource = Path.Combine(root, "ComfyUI"),
            VenvPath = Path.Combine(root, "venv"),
            PythonExecutable = Path.Combine(root, "venv", "python.exe"),
            Port = 8188,
            Status = "stopped",
        };
        repo.Upsert(env);
        return env;
    }

    private static EnvironmentListViewModel MakeSut(
        TestDb db, FakeComfyUIManagerInstaller? fakeInstaller = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        return new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!,
            fakeInstaller ?? new FakeComfyUIManagerInstaller());
    }

    private sealed class FakeComfyUIManagerInstaller : ComfyUIManagerInstaller
    {
        public NodeOperationResult NextResult { get; set; } = NodeOperationResult.Ok(null);
        public int InstallCallCount { get; private set; }
        public int UninstallCallCount { get; private set; }
        public IReadOnlyList<string>? CapturedProgress { get; private set; }

        public FakeComfyUIManagerInstaller() : base(new RequirementsFileInstaller(), "git") { }

        public override Task<NodeOperationResult> InstallAsync(
            Environment env, IProgress<string>? progress, CancellationToken ct)
        {
            InstallCallCount++;
            progress?.Report("fake-clone");
            progress?.Report("fake-pip");
            CapturedProgress = new[] { "fake-clone", "fake-pip" };
            // 只有成功时才创建目录,跟真 ComfyUIManagerInstaller 行为一致(失败 → 回滚删目录)。
            if (NextResult.Success)
            {
                var dir = ResolveTargetDirectory(env);
                if (dir is not null) Directory.CreateDirectory(dir);
            }
            return Task.FromResult(NextResult);
        }

        public override NodeOperationResult Uninstall(Environment env)
        {
            UninstallCallCount++;
            // 让 EnvListVM 末尾的 IsInstalled(env) 重测返 False:删 Manager 目录。
            var dir = ResolveTargetDirectory(env);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return NextResult;
        }
    }
}
