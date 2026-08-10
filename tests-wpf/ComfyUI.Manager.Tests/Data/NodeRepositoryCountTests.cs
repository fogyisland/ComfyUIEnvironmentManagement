using System;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class NodeRepositoryCountTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CountAllAsync_EmptyDb_ReturnsZero()
    {
        var repo = new NodeRepository(_db.Factory);

        var count = await repo.CountAllAsync();

        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task CountAllAsync_NonEmptyDb_ReturnsCount()
    {
        var repo = new NodeRepository(_db.Factory);
        for (var i = 0; i < 5; i++)
        {
            repo.Upsert(new ScannedNode
            {
                Id = $"node-{i}",
                EnvId = "env-1",
                Package = $"package-{i}",
                PackagePath = $"/tmp/package-{i}",
            });
        }

        var count = await repo.CountAllAsync();

        Assert.Equal(5L, count);
    }

    [Fact]
    public void Upsert_DownloadSource_AllowsSamePackageInDifferentEnv()
    {
        // G11:同包先 download(env_id="", source="download") → 再 env 装(env_id="env-1", source="env")
        // 不应冲突。新唯一索引 (env_id, package, source) 区分二者。
        // 注:用不同 id 才能真正共存 — 相同 id 的两条会被 ON CONFLICT(id) 覆盖式 update;
        // 这里分别走 download 路径(EnvId="")和 env 装路径(EnvId="env-1"),
        // 验的是 schema 层不冲突(env_id, package, source) 三元组不同。
        var repo = new NodeRepository(_db.Factory);

        repo.Upsert(new ScannedNode
        {
            Id = "node-x",
            EnvId = "",
            Package = "node-x",
            PackagePath = "/local/node-x",
            Source = "download",
        });
        // 用 env-1 prefix 的不同 id 模拟同包在 env 装(真实场景下 env 装会覆盖同 id 行,
        // 但 schema 层我们要证明:不同 (env_id, source) 元组不冲突)
        repo.Upsert(new ScannedNode
        {
            Id = "env-1:node-x",
            EnvId = "env-1",
            Package = "node-x",
            PackagePath = "/env/custom_nodes/node-x",
            Source = "env",
        });

        Assert.Equal(2L, repo.CountAllAsync().GetAwaiter().GetResult());

        var dl = repo.Get("node-x");
        Assert.NotNull(dl);
        Assert.Equal("", dl!.EnvId);
        Assert.Equal("download", dl.Source);

        var envNode = repo.Get("env-1:node-x");
        Assert.NotNull(envNode);
        Assert.Equal("env-1", envNode!.EnvId);
        Assert.Equal("env", envNode.Source);
    }

    [Fact]
    public void Upsert_TwoDownloads_SamePackage_Allowed()
    {
        // G11:两次 download 同包,新唯一索引允许(env_id, package, source) 三元组相等 → 唯一冲突
        // 退化为 ON CONFLICT(id) 覆盖式 update(同 id)。
        var repo = new NodeRepository(_db.Factory);

        repo.Upsert(new ScannedNode
        {
            Id = "node-x",
            EnvId = "",
            Package = "node-x",
            PackagePath = "/local/node-x",
            Version = "aaa",
            Source = "download",
        });
        // 不抛异常,覆盖式 update
        repo.Upsert(new ScannedNode
        {
            Id = "node-x",
            EnvId = "",
            Package = "node-x",
            PackagePath = "/local/node-x",
            Version = "bbb",
            Source = "download",
        });

        var node = repo.Get("node-x");
        Assert.NotNull(node);
        Assert.Equal("bbb", node!.Version);
        Assert.Equal("download", node.Source);
    }

    [Fact]
    public void Upsert_NewSchemaColumn_DefaultsToEnv()
    {
        // G11:未显式赋 Source 走 default "env";老数据 backfill 走 EnsureColumn 同样为 "env"
        var repo = new NodeRepository(_db.Factory);

        repo.Upsert(new ScannedNode
        {
            Id = "node-y",
            EnvId = "env-1",
            Package = "node-y",
            PackagePath = "/env/custom_nodes/node-y",
        });

        var node = repo.Get("node-y");
        Assert.NotNull(node);
        Assert.Equal("env", node!.Source);
    }
}
