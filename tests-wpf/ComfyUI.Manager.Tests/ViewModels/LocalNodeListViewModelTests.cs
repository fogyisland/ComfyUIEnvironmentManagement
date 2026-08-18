using System;
using System.IO;
using System.Linq;
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

public class LocalNodeListViewModelTests : IDisposable
{
    private readonly TestDb _db;
    private readonly NodeRepository _nodeRepo;
    private readonly EnvironmentRepository _envRepo;
    private readonly Settings _settings;
    private readonly string _localDir;
    private readonly GitRunner _git;
    private readonly NodeOperations _nodeOps;
    private readonly LocalNodeService _svc;
    private readonly LocalNodeCopyInstaller _installer;
    private readonly FakeRequirementsInstaller _reqInstaller;
    private readonly LocalNodeListViewModel _vm;
    private readonly ErrorBannerViewModel _errorBanner;

    public LocalNodeListViewModelTests()
    {
        _db = new TestDb();
        _localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localDir);
        _nodeRepo = new NodeRepository(new SqliteConnectionFactory(_db.Path));
        _envRepo = new EnvironmentRepository(new SqliteConnectionFactory(_db.Path));
        _settings = new Settings { LocalNodeDirectory = _localDir };
        _git = new GitRunner("git");
        _nodeOps = new NodeOperations(
            _git, _envRepo, _nodeRepo, _settings,
            new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))));
        _svc = new LocalNodeService(_settings, _nodeRepo, _envRepo, _nodeOps, logger: null);
        _installer = new LocalNodeCopyInstaller(_envRepo, _nodeRepo, _nodeOps, logger: null);
        _reqInstaller = new FakeRequirementsInstaller();
        _errorBanner = new ErrorBannerViewModel();
        _vm = new LocalNodeListViewModel(
            _svc, _installer, _envRepo, _nodeRepo, _reqInstaller, _errorBanner);
    }
    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
    }

    /// <summary>
    /// v0.6.15.6:fake RequirementsInstaller — override InstallNodeRequirementsAsync
    /// 让 VM 测试不真跑 pip,只记录调用 + 返回可控结果。
    /// </summary>
    private sealed class FakeRequirementsInstaller : RequirementsInstaller
    {
        public int InstallNodeReqCallCount { get; private set; }
        public Environment? LastEnv { get; private set; }
        public string? LastNodeDir { get; private set; }
        public RequirementsInstallResult NextResult { get; set; } =
            new(true, false, "节点无 requirements.txt", 0);

        public override Task<RequirementsInstallResult> InstallNodeRequirementsAsync(
            Environment env, string nodeDir,
            IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            InstallNodeReqCallCount++;
            LastEnv = env;
            LastNodeDir = nodeDir;
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task RefreshCommand_PopulatesItems()
    {
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-a"));

        await _vm.RefreshAsync();

        Assert.Single(_vm.Items);
        Assert.Equal("pkg-a", _vm.Items[0].Info.NodeId);
    }

    [Fact]
    public async Task InstallCommand_PickerCancels_DoesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-b"));
        await _vm.RefreshAsync();
        // picker 返 null = 取消
        _vm.EnvPickerOverride = (_, _) => null;

        await _vm.InstallAsync(_vm.Items[0].Info);

        Assert.Empty(_vm.Items[0].Info.InstalledEnvIds);  // 未装
    }

    [Fact]
    public async Task InstallCommand_PickerSelectsEnv_CopiesAndAppendsBadge()
    {
        var envCustomNodes = Path.Combine(Path.GetTempPath(), "env1-cn-" + Guid.NewGuid().ToString("N"));
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", CustomNodesPath = envCustomNodes });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-c"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-c", "f.txt"), "x");
        await _vm.RefreshAsync();
        // 模拟 env picker 选 env-1
        _vm.EnvPickerOverride = (_, envs) => envs.Single(e => e.Id == "env-1");

        await _vm.InstallAsync(_vm.Items[0].Info);

        Assert.Equal(new[] { "env-1" }, _vm.Items[0].Info.InstalledEnvIds);
        Assert.Equal(new[] { "prod" }, _vm.Items[0].Info.InstalledEnvNames);
        Assert.Contains("prod", _vm.Items[0].BadgeText);
    }

    [Fact]
    public async Task DeleteCommand_AfterConfirm_RemovesItem()
    {
        _vm.ConfirmDialogOverride = (_, _, _) => true;  // 用户确认
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-d"));
        await _vm.RefreshAsync();

        await _vm.DeleteAsync(_vm.Items[0].Info);

        Assert.Empty(_vm.Items);
        Assert.False(Directory.Exists(Path.Combine(_localDir, "pkg-d")));
    }

    [Fact]
    public async Task DeleteCommand_AfterCancel_KeepsItem()
    {
        _vm.ConfirmDialogOverride = (_, _, _) => false;  // 用户取消
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-e"));
        await _vm.RefreshAsync();

        await _vm.DeleteAsync(_vm.Items[0].Info);

        Assert.Single(_vm.Items);
    }

    // ──────────────── v0.6.15.6:"已装走 info banner" + "复制成功自动装节点依赖" ────────────────

    [Fact]
    public async Task InstallAsync_NodeAlreadyInEnv_ShowsInfoBanner_NoInstallerCall()
    {
        // env 已装 pkg-x (ScannedNode 行存在) → 重复点复制 → info banner,不动 installer
        var envCustomNodes = Path.Combine(Path.GetTempPath(), "cn-" + Guid.NewGuid().ToString("N"));
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", CustomNodesPath = envCustomNodes });
        Directory.CreateDirectory(Path.Combine(envCustomNodes, "pkg-x"));  // 目录已存在
        _nodeRepo.Upsert(new ScannedNode
        {
            Id = "pkg-x", EnvId = "env-1", Package = "pkg-x",
            PackagePath = Path.Combine(envCustomNodes, "pkg-x"),
            Status = "enabled", Source = "env",
            LastScannedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-x"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-x", "f.txt"), "x");
        await _vm.RefreshAsync();
        _vm.EnvPickerOverride = (_, envs) => envs.Single(e => e.Id == "env-1");

        await _vm.InstallAsync(_vm.Items[0].Info);
        // 等等 RunNodeRequirementsInstallAsync 的 fire-and-forget 异步跑完
        await Task.Delay(50);

        // 唯一 banner 应是 Info 级,含"已在 env" + env 名
        Assert.Single(_errorBanner.Entries);
        Assert.Equal(ErrorSeverity.Info, _errorBanner.Entries[0].Severity);
        Assert.Contains("pkg-x", _errorBanner.Entries[0].Message);
        Assert.Contains("prod", _errorBanner.Entries[0].Message);
        Assert.Contains("已在 env", _errorBanner.Entries[0].Message);
        // 没调 installer,也没触发 req install
        Assert.Null(_vm.NodeRequirementsStatus);
    }

    [Fact]
    public async Task InstallAsync_NodeNotInEnv_CopySuccess_TriggersNodeRequirementsInstall()
    {
        var envCustomNodes = Path.Combine(Path.GetTempPath(), "cn-" + Guid.NewGuid().ToString("N"));
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", CustomNodesPath = envCustomNodes });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-y"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-y", "f.txt"), "y");
        await _vm.RefreshAsync();
        _vm.EnvPickerOverride = (_, envs) => envs.Single(e => e.Id == "env-1");

        await _vm.InstallAsync(_vm.Items[0].Info);
        // fire-and-forget 的 RunNodeRequirementsInstallAsync → RunAsync;等它完成
        await Task.Delay(50);

        Assert.Empty(_errorBanner.Entries);  // 没有任何错误
        Assert.NotNull(_vm.NodeRequirementsStatus);
        Assert.Equal(1, _reqInstaller.InstallNodeReqCallCount);
        // production 传 targetDir(full path)给 InstallNodeRequirementsAsync,因为它内部
        // 要 Path.Combine(nodeDir, "requirements.txt")。test 验路径末尾等于 nodeId + 是 cn 子目录。
        Assert.NotNull(_reqInstaller.LastNodeDir);
        Assert.Equal("pkg-y", Path.GetFileName(_reqInstaller.LastNodeDir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.StartsWith(envCustomNodes, _reqInstaller.LastNodeDir);
        Assert.NotNull(_reqInstaller.LastEnv);
        Assert.Equal("env-1", _reqInstaller.LastEnv!.Id);
        // DB 行写入了
        Assert.NotNull(_nodeRepo.Get("pkg-y"));
        Assert.Equal("env-1", _nodeRepo.Get("pkg-y")!.EnvId);
    }

    [Fact]
    public async Task InstallAsync_CopySuccess_NodeReqFail_DoesNotBlockOrShowError()
    {
        // 用户原话:复制成功算 OK,req 失败只 WARN 日志,不回滚。
        var envCustomNodes = Path.Combine(Path.GetTempPath(), "cn-" + Guid.NewGuid().ToString("N"));
        _envRepo.Upsert(new Environment { Id = "env-1", Name = "prod", CustomNodesPath = envCustomNodes });
        Directory.CreateDirectory(Path.Combine(_localDir, "pkg-z"));
        File.WriteAllText(Path.Combine(_localDir, "pkg-z", "f.txt"), "z");
        await _vm.RefreshAsync();
        _vm.EnvPickerOverride = (_, envs) => envs.Single(e => e.Id == "env-1");
        // fake req 返 pip 失败
        _reqInstaller.NextResult = new RequirementsInstallResult(
            false, false, "pip 退出码 1", 0);

        await _vm.InstallAsync(_vm.Items[0].Info);
        await Task.Delay(50);

        Assert.Empty(_errorBanner.Entries);  // 关键:req 失败不进 error banner
        Assert.NotNull(_vm.NodeRequirementsStatus);  // 面板 VM 仍然挂上(用户可看到 pip 错误)
        Assert.True(_vm.NodeRequirementsStatus!.HasError);
        Assert.Contains("pip 退出码", _vm.NodeRequirementsStatus.Error);
        // DB 行仍然写入(没回滚)
        Assert.NotNull(_nodeRepo.Get("pkg-z"));
    }
}