using System;
using System.IO;
using System.Linq;
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
        _errorBanner = new ErrorBannerViewModel();
        _vm = new LocalNodeListViewModel(_svc, _installer, _envRepo, _errorBanner);
    }
    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_localDir)) Directory.Delete(_localDir, recursive: true);
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
}