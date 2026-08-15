using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentDetailViewModelDeleteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly NodeRepository _repo;

    public EnvironmentDetailViewModelDeleteTests()
    {
        // 跟 EnvironmentRepositoryTests 同款:temp file + factory 共享路径,
        // 让 Upsert / ListByEnv 跨连接持久化(:memory: 跨 Open() 不共享,
        // 而 InitSchemaIfMissing 是 private 不可直接调)。
        _dbPath = Path.Combine(Path.GetTempPath(),
            "env-detail-delete-" + Path.GetRandomFileName() + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new NodeRepository(_factory);
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private void Seed(ScannedNode node)
    {
        // 触发 schema init(第一次 Open() 跑 CREATE TABLE IF NOT EXISTS)
        using (var conn = _factory.Open())
        {
            // conn 此时 schema 已就位,留到 Dispose 自动释放
        }
        _repo.Upsert(node);
    }

    [Fact]
    public async Task DeleteAsync_AfterConfirm_RemovesNodeFromCollection()
    {
        Seed(new ScannedNode
        {
            Id = "n1", EnvId = "e1", Package = "n1", Status = "enabled",
            Source = "env",
        });
        var deleteCalls = 0;
        var vm = new EnvironmentDetailViewModel(_repo, new ErrorBannerViewModel(),
            (_, _, _) =>
            {
                deleteCalls++;
                return Task.FromResult(new NodeOperationResult(true, null, "abc123"));
            },
            "e1")
        {
            ConfirmDialogOverride = (_, _, _) => true,
        };
        Assert.Single(vm.Nodes);

        await vm.DeleteAsync(vm.Nodes[0]);

        Assert.Empty(vm.Nodes);       // VM 集合移除
        Assert.Equal(1, deleteCalls); // deleteFunc 被调
    }

    [Fact]
    public async Task DeleteAsync_AfterCancel_LeavesNodeIntact()
    {
        Seed(new ScannedNode
        {
            Id = "n1", EnvId = "e1", Package = "n1", Status = "enabled",
            Source = "env",
        });
        var deleteCalls = 0;
        var vm = new EnvironmentDetailViewModel(_repo, new ErrorBannerViewModel(),
            (_, _, _) =>
            {
                deleteCalls++;
                return Task.FromResult(new NodeOperationResult(true, null, null));
            },
            "e1")
        {
            ConfirmDialogOverride = (_, _, _) => false,  // 用户取消
        };

        await vm.DeleteAsync(vm.Nodes[0]);

        Assert.Single(vm.Nodes);       // 行还在
        Assert.Equal(0, deleteCalls);  // deleteFunc 没被调
    }

    [Fact]
    public async Task DeleteAsync_UninstallFails_KeepsNodeAndAddsErrorBanner()
    {
        Seed(new ScannedNode
        {
            Id = "n1", EnvId = "e1", Package = "n1", Status = "enabled",
            Source = "env",
        });
        var errorBanner = new ErrorBannerViewModel();
        var vm = new EnvironmentDetailViewModel(_repo, errorBanner,
            (_, _, _) => Task.FromResult(new NodeOperationResult(false, "目录被占用", null)),
            "e1")
        {
            ConfirmDialogOverride = (_, _, _) => true,
        };

        await vm.DeleteAsync(vm.Nodes[0]);

        Assert.Single(vm.Nodes);        // 失败保留
        Assert.Single(errorBanner.Entries);  // 弹 error banner
    }
}
