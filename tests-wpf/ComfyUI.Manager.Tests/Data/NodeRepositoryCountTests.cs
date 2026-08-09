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
}
