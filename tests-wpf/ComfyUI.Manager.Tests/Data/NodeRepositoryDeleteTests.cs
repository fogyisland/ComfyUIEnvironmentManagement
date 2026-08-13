using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// T1 picker redesign:NodeRepository.Delete 直接走 SQLite DELETE,不级联,不抛异常。
/// </summary>
public sealed class NodeRepositoryDeleteTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Delete_ExistingRow_RemovesRow()
    {
        var repo = new NodeRepository(_db.Factory);
        repo.Upsert(new ScannedNode
        {
            Id = "node-a",
            EnvId = "env-1",
            Package = "node-a",
            PackagePath = "/tmp/node-a",
        });
        Assert.NotNull(repo.Get("node-a"));

        repo.Delete("node-a");

        Assert.Null(repo.Get("node-a"));
    }

    [Fact]
    public void Delete_NonExistent_NoOp()
    {
        var repo = new NodeRepository(_db.Factory);
        // 不抛异常
        repo.Delete("nope");
        Assert.Null(repo.Get("nope"));
    }
}
