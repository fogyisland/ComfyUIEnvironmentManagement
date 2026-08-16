using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class NodeRepositoryGetByPackageNameTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NodeRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly string _envId = "env-1";

    public NodeRepositoryGetByPackageNameTests()
    {
        _repo = new NodeRepository(_db.Factory);
        _envRepo = new EnvironmentRepository(_db.Factory);
        // Seed env row 满足 scanned_nodes.env_id FK
        _envRepo.Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = _envId,
            Name = "test-env-" + _envId,
            RootPath = "/x/" + _envId,
            ComfyuiLayout = "standalone",
        });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void GetByPackageName_ExistingRow_ReturnsNode()
    {
        _repo.Upsert(new ScannedNode
        {
            Id = "node-1",
            EnvId = _envId,
            Package = "comfyui-impact-pack",
            PackagePath = "/x/" + _envId + "/ComfyUI-Impact-Pack",
            Source = "env",
        });
        var found = _repo.GetByPackageName(_envId, "comfyui-impact-pack");
        Assert.NotNull(found);
        Assert.Equal("node-1", found!.Id);
        Assert.Equal("comfyui-impact-pack", found.Package);
    }

    [Fact]
    public void GetByPackageName_DifferentEnv_ReturnsNull()
    {
        _envRepo.Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = "other-env",
            Name = "other-env",
            RootPath = "/x/other",
            ComfyuiLayout = "standalone",
        });
        _repo.Upsert(new ScannedNode
        {
            Id = "node-1",
            EnvId = "other-env",
            Package = "comfyui-impact-pack",
            PackagePath = "/x/other/cust-nodes/x",
            Source = "env",
        });
        var found = _repo.GetByPackageName(_envId, "comfyui-impact-pack");
        Assert.Null(found);
    }

    [Fact]
    public void GetByPackageName_MissingRow_ReturnsNull()
    {
        var found = _repo.GetByPackageName(_envId, "nonexistent-package");
        Assert.Null(found);
    }
}
