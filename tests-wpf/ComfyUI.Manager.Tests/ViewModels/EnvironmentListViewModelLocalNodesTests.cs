using System;
using System.IO;
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

/// <summary>
/// v1.0.0.x #577:EnvironmentListViewModel「安装本地常用」+ 启停合按钮 测试 — 覆盖 Load()
/// 末尾重算 4 个字段(IsLocalNodesInstalled / LocalNodesButtonText / StartStopButtonText /
/// StartStopButtonEnabled)、InstallLocalNodesCommand busy guard、LocalNodeInstallStatus
/// panel begin/complete/fail 三态、StartStopCommand 根据 Status 派发。
///
/// <para>
/// 镜像 <c>EnvironmentListViewModelComfyUiManagerTests</c> 模式 — TestDb + 真实
/// <see cref="LocalNodeBulkInstaller"/>(用临时 src dir,无 pip/copy 副作用)+ <c>SetEnvBusyForTest</c>
/// 模拟长操作占用(RelayCommand.CanExecute 锁)。
/// </para>
/// </summary>
public sealed class EnvironmentListViewModelLocalNodesTests : IDisposable
{
    private readonly string _srcDir;

    public EnvironmentListViewModelLocalNodesTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "lnbi-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_srcDir)) Directory.Delete(_srcDir, recursive: true); } catch { }
    }

    // ───── Load() 重算 ─────

    [Fact]
    public void Load_NoLocalNodesInstalled_DefaultsToInstallButton()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "stopped");
        var sut = MakeSut(db);

        Assert.False(sut.Environments[0].IsLocalNodesInstalled);
        Assert.Equal("安装本地常用", sut.Environments[0].LocalNodesButtonText);
    }

    [Fact]
    public void Load_AllLocalNodesInstalled_ShowsReinstallButton()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1", status: "stopped");
        // 源 = 1 个包 + env.custom_nodes 已有同名目录 → IsInstalled true
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        Directory.CreateDirectory(Path.Combine(env.RootPath, "ComfyUI", "custom_nodes", "pkg-a"));

        var sut = MakeSut(db);

        Assert.True(sut.Environments[0].IsLocalNodesInstalled);
        Assert.Equal("重装本地常用", sut.Environments[0].LocalNodesButtonText);
    }

    [Fact]
    public void Load_StoppedEnv_StartStopButtonShowsStart()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "stopped");
        var sut = MakeSut(db);

        Assert.Equal("启动", sut.Environments[0].StartStopButtonText);
        Assert.True(sut.Environments[0].StartStopButtonEnabled);
    }

    [Fact]
    public void Load_RunningEnv_StartStopButtonShowsStop()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "running");
        var sut = MakeSut(db);

        Assert.Equal("停止", sut.Environments[0].StartStopButtonText);
        Assert.True(sut.Environments[0].StartStopButtonEnabled);
    }

    [Fact]
    public void Load_MidStateEnv_StartStopButtonDisabled()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "starting");
        var sut = MakeSut(db);

        // mid-state(starting/stopping)按钮 disabled;text 保留 "启动" 因为 Status != "running"
        Assert.False(sut.Environments[0].StartStopButtonEnabled);
    }

    // ───── InstallLocalNodesCommand CanExecute ─────

    [Fact]
    public void InstallLocalNodesCommand_EnabledWhenIdle()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        Assert.True(sut.InstallLocalNodesCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public void InstallLocalNodesCommand_DisabledWhenBusy()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1");
        var sut = MakeSut(db);

        sut.SetEnvBusyForTest(sut.Environments[0]);
        Assert.False(sut.InstallLocalNodesCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public void InstallLocalNodesCommand_DisabledWhenComfyuiSourceEmpty()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        env.ComfyuiSource = "";  // 故意清空让 CanExecute 返 false
        var sut = MakeSut(db);

        Assert.False(sut.InstallLocalNodesCommand.CanExecute(env));
    }

    // ───── StartStopCommand CanExecute ─────

    [Fact]
    public void StartStopCommand_EnabledWhenStoppedAndIdle()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "stopped");
        var sut = MakeSut(db);

        Assert.True(sut.StartStopCommand.CanExecute(sut.Environments[0]));
    }

    [Fact]
    public void StartStopCommand_DisabledWhenBusy()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-1", status: "stopped");
        var sut = MakeSut(db);

        sut.SetEnvBusyForTest(sut.Environments[0]);
        Assert.False(sut.StartStopCommand.CanExecute(sut.Environments[0]));
    }

    // ───── InstallLocalNodesAsync 集成 ─────

    [Fact]
    public async Task InstallLocalNodesAsync_Success_UpdatesButtonAndStatus()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "code.py"), "a");
        var sut = MakeSut(db);

        await sut.InstallLocalNodesAsync(env);

        Assert.True(sut.Environments[0].IsLocalNodesInstalled);
        Assert.Equal("重装本地常用", sut.Environments[0].LocalNodesButtonText);
        Assert.NotNull(sut.LocalNodeInstallStatus);
        Assert.True(sut.LocalNodeInstallStatus!.IsComplete);
        Assert.False(sut.LocalNodeInstallStatus.HasError);
    }

    [Fact]
    public async Task InstallLocalNodesAsync_Failure_SetsStatusError_NoButtonChange()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        // src dir 空 → IsInstalled() 后检返回 false(无包可装)
        var sut = MakeSut(db);

        await sut.InstallLocalNodesAsync(env);

        Assert.False(sut.Environments[0].IsLocalNodesInstalled);
        Assert.Equal("安装本地常用", sut.Environments[0].LocalNodesButtonText);  // 失败 → 不切到"重装"
        Assert.NotNull(sut.LocalNodeInstallStatus);
        Assert.True(sut.LocalNodeInstallStatus!.HasError);
        Assert.Contains("本地节点目录为空", sut.LocalNodeInstallStatus.Error);
    }

    [Fact]
    public async Task InstallLocalNodesAsync_EnvBusy_NoOp()
    {
        using var db = new TestDb();
        var env = SeedEnv(db, "env-1");
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        var sut = MakeSut(db);
        sut.SetEnvBusyForTest(env);

        // 状态面板不动(没开始过)
        Assert.Null(sut.LocalNodeInstallStatus);
        var target = Path.Combine(env.RootPath, "ComfyUI", "custom_nodes", "pkg-a");
        Assert.False(Directory.Exists(target));  // busy guard → 没 copy

        await sut.InstallLocalNodesAsync(env);

        Assert.Null(sut.LocalNodeInstallStatus);
        Assert.False(Directory.Exists(target));  // 仍然没 copy
    }

    // ───── Helpers ─────

    private static Environment SeedEnv(TestDb db, string id, string status = "stopped")
    {
        var root = Path.Combine(Path.GetTempPath(), $"localnodes-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var comfyuiSource = Path.Combine(root, "ComfyUI");
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            ComfyuiSource = comfyuiSource,
            // LocalNodeBulkInstaller 直接读 env.CustomNodesPath(不从 ComfyuiSource 派生),
            // 必须显式设。跟生产 EnvCreatorService 派生路径一致(comfyuiSource/custom_nodes)。
            CustomNodesPath = Path.Combine(comfyuiSource, "custom_nodes"),
            VenvPath = Path.Combine(root, "venv"),
            PythonExecutable = Path.Combine(root, "venv", "python.exe"),
            Port = 8188,
            Status = status,
            BedStatus = "done",  // 已装 BED → 启停按钮 enabled(stopped → "启动")
        };
        repo.Upsert(env);
        return env;
    }

    private EnvironmentListViewModel MakeSut(TestDb db)
    {
        var repo = new EnvironmentRepository(db.Factory);
        var settings = new Settings { LocalNodesDirectory = _srcDir };
        var installer = new LocalNodeBulkInstaller(settings, new RequirementsFileInstaller());
        return new EnvironmentListViewModel(
            repo, null!, null!, null!, settings, null!, null!, null!, null!,
            null!, null!, null!, null!, null!,
            localNodeBulkInstaller: installer);
    }
}