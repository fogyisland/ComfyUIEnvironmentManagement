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

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelRequirementsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _projectRoot;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly CatalogRepository _catRepo;
    private readonly NodeVersionRepository _versionRepo;
    private readonly FakeRefreshService _refreshService;
    private readonly NoopNodeOps _nodeOps;

    public CatalogViewModelRequirementsTests()
    {
        _db = new TestDb();
        _projectRoot = Path.Combine(Path.GetTempPath(), $"cat-vm-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _settings = new Settings();
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, _projectRoot);
        _settingsRepo = new SettingsRepository(
            Path.Combine(_projectRoot, "settings.json"));
        var cacheStore = new CatalogCacheStore(_db.Path);
        _catRepo = new CatalogRepository(cacheStore);
        _versionRepo = new NodeVersionRepository(cacheStore);
        _refreshService = new FakeRefreshService();
        _nodeOps = new NoopNodeOps(
            new EnvironmentRepository(_db.Factory),
            new NodeRepository(_db.Factory),
            _settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(
            _catRepo, _versionRepo, _nodeOps, _refreshService,
            _settings, _settingsRepo, _projectRoot);

    private void SeedEntry(string package, Dictionary<string, object?> rawMetadata)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            RawMetadata = rawMetadata,
            CachedAt = "2026-08-07T00:00:00Z",
            ExpiresAt = "2026-08-08T00:00:00Z",
        });
    }

    [Fact]
    public void SelectedPipRequirements_PopulatedFromDb()
    {
        SeedEntry("pkg-vm", new Dictionary<string, object?>
        {
            ["author"] = "alice",
            ["pip"] = new List<object?> { "numpy>=1.24.0", "huggingface-hub" },
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First();

        Assert.Equal(2, vm.SelectedPipRequirements.Count);
        Assert.Equal("numpy", vm.SelectedPipRequirements[0].Name);
        Assert.Equal(">=1.24.0", vm.SelectedPipRequirements[0].Specifier);
        Assert.Equal("huggingface-hub", vm.SelectedPipRequirements[1].Name);
        Assert.Null(vm.SelectedPipRequirements[1].Specifier);
    }

    [Fact]
    public void HasPipRequirements_True_WhenAny()
    {
        SeedEntry("pkg-pip", new Dictionary<string, object?>
        {
            ["pip"] = new List<object?> { "torch" },
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-pip");

        Assert.True(vm.HasPipRequirements);
    }

    [Fact]
    public void HasPipRequirements_False_WhenNoPipField()
    {
        SeedEntry("pkg-no-pip", new Dictionary<string, object?>
        {
            ["author"] = "bob",
        });

        var vm = NewVm();
        vm.Selected = vm.PagedEntries.First(e => e.Package == "pkg-no-pip");

        Assert.False(vm.HasPipRequirements);
        Assert.Empty(vm.SelectedPipRequirements);
    }

    /// <summary>
    /// Fake refresh service — 同 CatalogViewModelTests.FakeRefreshService pattern,
    /// 不真跑 fetch。继承 CatalogRefreshService 调 base(null fetcher, default settings)。
    /// </summary>
    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public FakeRefreshService()
            : base(new NullCatalogFetcher(),
                   new CatalogRepository(new CatalogCacheStore(Path.Combine(
                       Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db"))),
                   new Settings())
        { }
    }

    private sealed class NullCatalogFetcher : CatalogFetcher
    {
        public NullCatalogFetcher() : base(new System.Net.Http.HttpClient(), 60) { }
        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings,
                   new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ComfyUI.Manager.Infrastructure.ProcessResult(true, 0, "[]", "")))) { }
    }
}