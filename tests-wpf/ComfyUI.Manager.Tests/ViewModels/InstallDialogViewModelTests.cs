using System;
using System.Collections.Generic;
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

/// <summary>
/// v0.6.11 T3: InstallDialogViewModel 接受 ctor <c>preselectedTag</c> 并在
/// InstallAsync 时把 tag 传给 NodeOperations.InstallAsync 的 targetTag 参数。
/// 既向后兼容(不传 tag = 行为同 v0.6.10.x),又允许 caller 显式选版本。
/// </summary>
public class InstallDialogViewModelTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;

    public InstallDialogViewModelTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeRepo = new NodeRepository(_db.Factory);
        SeedEnv("env-1");
    }

    public void Dispose() => _db.Dispose();

    private void SeedEnv(string id)
    {
        _envRepo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = "stopped",
        });
    }

    private static CatalogEntry MakeEntry()
    {
        return new CatalogEntry
        {
            Id = "node-1",
            Package = "ComfyUI-Test",
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/owner/test",
            },
        };
    }

    [Fact]
    public void Ctor_PreselectedTagNull_DefaultsToNull()
    {
        var fakeOps = new CapturingNodeOps(_envRepo, _nodeRepo, _settings);
        var vm = new InstallDialogViewModel(_envRepo, fakeOps, MakeEntry());

        Assert.Null(vm.PreselectedTag);
    }

    [Fact]
    public void Ctor_PreselectedTag_ExposedOnProperty()
    {
        var fakeOps = new CapturingNodeOps(_envRepo, _nodeRepo, _settings);
        var vm = new InstallDialogViewModel(_envRepo, fakeOps, MakeEntry(),
            preselectedEnvId: "env-1", preselectedTag: "v1.2.3");

        Assert.Equal("v1.2.3", vm.PreselectedTag);
    }

    [Fact]
    public async Task InstallCommand_PreselectedTag_PassesTargetTagToNodeOps()
    {
        var fakeOps = new CapturingNodeOps(_envRepo, _nodeRepo, _settings);
        var vm = new InstallDialogViewModel(_envRepo, fakeOps, MakeEntry(),
            preselectedEnvId: "env-1", preselectedTag: "v2.0.0");

        vm.InstallCommand.Execute(null);

        // Command runs async via RelayCommand; wait for the captured call to land.
        var spin = 0;
        while (fakeOps.LastTargetTag is null && spin < 200)
        {
            await Task.Delay(10);
            spin++;
        }

        Assert.Equal("v2.0.0", fakeOps.LastTargetTag);
        Assert.Equal("env-1", fakeOps.LastEnvId);
        Assert.Equal("ComfyUI-Test", fakeOps.LastNodeId);
        Assert.Equal("https://github.com/owner/test", fakeOps.LastRepoUrl);
    }

    [Fact]
    public async Task InstallCommand_NoPreselectedTag_PassesNullTargetTag()
    {
        var fakeOps = new CapturingNodeOps(_envRepo, _nodeRepo, _settings);
        var vm = new InstallDialogViewModel(_envRepo, fakeOps, MakeEntry(),
            preselectedEnvId: "env-1");

        vm.InstallCommand.Execute(null);

        var spin = 0;
        while (!fakeOps.Called && spin < 200)
        {
            await Task.Delay(10);
            spin++;
        }

        Assert.True(fakeOps.Called);
        Assert.Null(fakeOps.LastTargetTag);
    }

    [Fact]
    public void Ctor_PreselectedEnvId_Null_PicksFirstEnv()
    {
        SeedEnv("env-2");
        var fakeOps = new CapturingNodeOps(_envRepo, _nodeRepo, _settings);
        var vm = new InstallDialogViewModel(_envRepo, fakeOps, MakeEntry());

        Assert.NotNull(vm.SelectedEnv);
        Assert.Equal("env-1", vm.SelectedEnv!.Id);  // first in repo
    }

    private sealed class CapturingNodeOps : NodeOperations
    {
        public string? LastEnvId { get; private set; }
        public string? LastNodeId { get; private set; }
        public string? LastRepoUrl { get; private set; }
        public string? LastTargetTag { get; private set; }
        public bool Called { get; private set; }

        public CapturingNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))))
        { }

        public override Task<NodeOperationResult> InstallAsync(
            string envId, string nodeId, string repoUrl,
            string? targetTag = null,
            IReadOnlyList<PipRequirement>? catalogPipReqs = null,
            CancellationToken ct = default)
        {
            Called = true;
            LastEnvId = envId;
            LastNodeId = nodeId;
            LastRepoUrl = repoUrl;
            LastTargetTag = targetTag;
            return Task.FromResult(NodeOperationResult.Ok("sha-fake"));
        }
    }
}
