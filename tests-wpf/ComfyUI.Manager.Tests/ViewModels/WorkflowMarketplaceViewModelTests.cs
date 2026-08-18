using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class WorkflowMarketplaceViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Settings _settings;
    private readonly StubMarketplaceService _marketplace;
    private readonly WorkflowDownloader _downloader;
    private readonly WorkflowMarketplaceViewModel _vm;

    public WorkflowMarketplaceViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFVm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settings = new Settings { WorkflowsDirectory = _tempDir };
        _marketplace = new StubMarketplaceService();
        _downloader = new WorkflowDownloader(new HttpClient(new OkHandler()), logger: null);
        _vm = new WorkflowMarketplaceViewModel(_settings, _marketplace, _downloader,
            new WorkflowFilesystemScanner(logger: null), logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static WorkflowEntry Entry(string id, WorkflowSourceKind src = WorkflowSourceKind.CommunityJson,
        string title = "T", string? author = null, string[]? tags = null, int? downloads = null,
        DateTimeOffset? published = null)
        => new() { Source = src, SourceId = id, SourceUrl = $"https://{src}/{id}",
                   WorkflowJsonUrl = $"https://{src}/{id}.json", Title = title,
                   Author = author, Tags = tags ?? Array.Empty<string>(),
                   DownloadCount = downloads, PublishedAt = published };

    [Fact]
    public async Task RefreshAsync_PopulatesWorkflows()
    {
        _marketplace.Next = new[] { Entry("a"), Entry("b"), Entry("c") };
        await _vm.RefreshAsync();
        Assert.Equal(3, _vm.Workflows.Count);
    }

    [Fact]
    public async Task RefreshAsync_ErrorMessage_OnException()
    {
        _marketplace.ThrowOnNext = new InvalidOperationException("boom");
        await _vm.RefreshAsync();
        Assert.NotNull(_vm.ErrorMessage);
        Assert.Contains("boom", _vm.ErrorMessage);
    }

    [Fact]
    public async Task SearchText_FiltersByTitle()
    {
        _marketplace.Next = new[]
        {
            Entry("a", title: "Apple"),
            Entry("b", title: "Banana"),
            Entry("c", title: "Cherry"),
        };
        await _vm.RefreshAsync();
        _vm.SearchText = "ban";
        Assert.Single(_vm.Workflows);
        Assert.Equal("b", _vm.Workflows[0].SourceId);
    }

    [Fact]
    public async Task ActiveSourceFilters_FilterBySource()
    {
        _marketplace.Next = new[]
        {
            Entry("a", src: WorkflowSourceKind.CommunityJson),
            Entry("b", src: WorkflowSourceKind.CivitAi),
            Entry("c", src: WorkflowSourceKind.OpenArt),
        };
        await _vm.RefreshAsync();
        _vm.ActiveSourceFilters.Clear();
        _vm.ActiveSourceFilters.Add(WorkflowSourceKind.CivitAi);
        Assert.Single(_vm.Workflows);
        Assert.Equal(WorkflowSourceKind.CivitAi, _vm.Workflows[0].Source);
    }

    [Fact]
    public async Task SortBy_Name_SortsAlphabetically()
    {
        _marketplace.Next = new[]
        {
            Entry("a", title: "Zebra"),
            Entry("b", title: "Apple"),
            Entry("c", title: "Mango"),
        };
        await _vm.RefreshAsync();
        _vm.SortBy = WorkflowSortKind.Name;
        Assert.Equal("Apple", _vm.Workflows[0].Title);
        Assert.Equal("Mango", _vm.Workflows[1].Title);
        Assert.Equal("Zebra", _vm.Workflows[2].Title);
    }

    [Fact]
    public async Task SortBy_Downloads_SortsByCount()
    {
        _marketplace.Next = new[]
        {
            Entry("a", downloads: 5),
            Entry("b", downloads: 100),
            Entry("c", downloads: 20),
        };
        await _vm.RefreshAsync();
        _vm.SortBy = WorkflowSortKind.Downloads;
        Assert.Equal("b", _vm.Workflows[0].SourceId);  // 100
        Assert.Equal("c", _vm.Workflows[1].SourceId);  // 20
        Assert.Equal("a", _vm.Workflows[2].SourceId);  // 5
    }

    [Fact]
    public void ToggleSelectAll_FlipsSelection()
    {
        _vm.Workflows.Add(Entry("a"));
        _vm.Workflows.Add(Entry("b"));
        _vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(2, _vm.Selected.Count);
        _vm.ToggleSelectAllCommand.Execute(null);
        Assert.Empty(_vm.Selected);
    }

    [Fact]
    public void BatchDownloadCommand_DisabledWhenNoSelection()
    {
        Assert.False(_vm.BatchDownloadCommand.CanExecute(null));
        _vm.Selected.Add(Entry("a"));
        // Without workflowsDir existing, also need it OK
        // (in our setup it exists, so should be enabled)
        Assert.True(_vm.BatchDownloadCommand.CanExecute(null));
    }

    private sealed class StubMarketplaceService : WorkflowMarketplaceService
    {
        public IReadOnlyList<WorkflowEntry>? Next { get; set; }
        public Exception? ThrowOnNext { get; set; }

        public StubMarketplaceService() : base(Array.Empty<IWorkflowSource>()) { }

        public override Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
        {
            if (ThrowOnNext is not null) throw ThrowOnNext;
            return Task.FromResult(Next ?? Array.Empty<WorkflowEntry>());
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
    }
}