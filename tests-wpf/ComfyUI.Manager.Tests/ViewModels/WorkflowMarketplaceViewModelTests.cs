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
    private readonly StubHttpHandler _httpHandler;
    private readonly HttpClient _http;
    private readonly StubMarketplaceService _marketplace;
    private readonly WorkflowDownloader _downloader;
    private readonly WorkflowMarketplaceViewModel _vm;

    public WorkflowMarketplaceViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFVm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settings = new Settings { WorkflowsDirectory = _tempDir };
        _httpHandler = new StubHttpHandler();
        _http = new HttpClient(_httpHandler);
        _marketplace = new StubMarketplaceService(httpClient: _http);
        _downloader = new WorkflowDownloader(_http, logger: null);
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

    // v0.6.19.x UI polish:IsEmpty 用于 empty-state overlay 可见性。
    // IsEmpty = !IsBusy && Workflows.Count == 0 && ErrorMessage is null
    [Fact]
    public void IsEmpty_NoBusyNoResultsNoError_ReturnsTrue()
    {
        Assert.True(_vm.IsEmpty);
    }

    [Fact]
    public async Task IsEmpty_BusyWhileFetching_ReturnsFalse()
    {
        // 配一个慢一点的 service(返回 TaskCompletionSource),先 IsBusy=true 再 assert
        var slowSvc = new SlowMarketplaceService();
        var vm = new WorkflowMarketplaceViewModel(_settings, slowSvc, _downloader,
            new WorkflowFilesystemScanner(logger: null), logger: null);
        var refreshTask = vm.RefreshAsync();
        // RefreshAsync 同步段立即设 IsBusy=true 然后 await,所以 await Yield 后 IsBusy 还是 true
        await Task.Yield();
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsEmpty);
        slowSvc.Complete();
        await refreshTask;
    }

    [Fact]
    public async Task IsEmpty_HasResults_ReturnsFalse()
    {
        _marketplace.Next = new[] { Entry("a") };
        await _vm.RefreshAsync();
        Assert.False(_vm.IsEmpty);
    }

    [Fact]
    public async Task IsEmpty_HasError_ReturnsFalse()
    {
        _marketplace.ThrowOnNext = new InvalidOperationException("boom");
        await _vm.RefreshAsync();
        Assert.False(_vm.IsEmpty);
    }

    // v0.6.22: ✕ clear button — sets SearchText to "" which triggers ApplyFilter
    // and HasSearchText recomputes to false (drives ✕ button visibility).
    [Fact]
    public void ClearSearchCommand_ClearsSearchText_AndAppliesFilter()
    {
        _vm.SearchText = "controlnet";
        Assert.True(_vm.HasSearchText);

        _vm.ClearSearchCommand.Execute(null);

        Assert.Equal("", _vm.SearchText);
        Assert.False(_vm.HasSearchText);
        // Note: ClearSearchCommand CanExecute = HasSearchText, so post-clear CanExecute = false.
        // RelayCommand's CanExecute.Invoke doesn't throw when predicate is false (idempotent).
        Assert.False(_vm.HasSearchText);
    }

    // v0.6.22 T3: hover → fetch workflow JSON → cache on entry.JsonPreview,
    // populate JsonOverlayText with pretty-printed JSON. Second hover = cache hit.
    [Fact]
    public async Task LoadJsonPreviewAsync_HoverEntry_FetchesAndCachesJson()
    {
        var entry = new WorkflowEntry
        {
            Source = WorkflowSourceKind.CivitAi,
            SourceId = "test-1",
            Title = "Test",
            WorkflowJsonUrl = "https://example.com/wf.json",
        };
        _httpHandler.RegisterResponse("https://example.com/wf.json", "{\"nodes\":[{\"id\":1}],\"links\":[]}");

        await _vm.LoadJsonPreviewAsync(entry);

        Assert.Equal(entry, _vm.HoveredEntry);
        Assert.NotNull(_vm.JsonOverlayText);   // pretty-printed
        Assert.NotNull(entry.JsonPreview);   // cached for subsequent hovers
        // second hover → cache hit, no additional HTTP call
        await _vm.LoadJsonPreviewAsync(entry);
        Assert.Equal(1, _httpHandler.RequestCount);
        // IsJsonOverlayVisible is true after success
        Assert.True(_vm.IsJsonOverlayVisible);
    }

    // v0.6.22 T3: mouse leave → Hide overlay, cache preserved for next hover.
    [Fact]
    public async Task ClearJsonOverlay_ClearsHoverState_AndJsonOverlayText()
    {
        var entry = new WorkflowEntry
        {
            Source = WorkflowSourceKind.CivitAi,
            SourceId = "test-1",
            Title = "Test",
            WorkflowJsonUrl = "https://example.com/wf.json",
        };
        _httpHandler.RegisterResponse("https://example.com/wf.json", "{\"nodes\":[]}");

        await _vm.LoadJsonPreviewAsync(entry);
        Assert.Equal(entry, _vm.HoveredEntry);

        _vm.ClearJsonOverlay();

        Assert.Null(_vm.HoveredEntry);
        Assert.Null(_vm.JsonOverlayText);
        Assert.False(_vm.IsJsonOverlayVisible);
        // cache preserved on entry
        Assert.NotNull(entry.JsonPreview);
    }

    // v0.6.22 T3: fetch failure → IsJsonOverlayError=true, HoveredEntry still set,
    // JsonOverlayText stays null. Failed entry doesn't cache.
    [Fact]
    public async Task LoadJsonPreviewAsync_OnFetchFailure_SetsErrorState()
    {
        var entry = new WorkflowEntry
        {
            Source = WorkflowSourceKind.CivitAi,
            SourceId = "broken",
            Title = "Broken",
            WorkflowJsonUrl = "https://example.com/404.json",
        };
        _httpHandler.RegisterStatus("https://example.com/404.json", HttpStatusCode.NotFound);

        await _vm.LoadJsonPreviewAsync(entry);

        Assert.Equal(entry, _vm.HoveredEntry);
        Assert.True(_vm.IsJsonOverlayError);
        Assert.Null(_vm.JsonOverlayText);
        Assert.False(_vm.IsJsonOverlayLoading);
        Assert.Null(entry.JsonPreview);   // failed entry doesn't cache — next hover retries
    }

    // v0.6.22 T3-R1: retry button on error state — re-invokes LoadJsonPreviewAsync;
    // clears error state and populates overlay if fetch succeeds this time.
    [Fact]
    public async Task RetryJsonPreviewCommand_OnError_RetriesFetch_AndClearsErrorState()
    {
        var entry = new WorkflowEntry
        {
            Source = WorkflowSourceKind.CivitAi,
            SourceId = "flaky",
            Title = "Flaky",
            WorkflowJsonUrl = "https://example.com/flaky.json",
        };
        // First call: 404 → error state
        _httpHandler.RegisterStatus("https://example.com/flaky.json", HttpStatusCode.NotFound);

        await _vm.LoadJsonPreviewAsync(entry);
        Assert.True(_vm.IsJsonOverlayError);
        Assert.Null(_vm.JsonOverlayText);
        Assert.Null(entry.JsonPreview);

        // CanExecute: valid entry with URL is allowed
        Assert.True(_vm.RetryJsonPreviewCommand.CanExecute(entry));

        // Re-register: clear status first, then now succeeds
        _httpHandler.ClearStatus("https://example.com/flaky.json");
        _httpHandler.RegisterResponse("https://example.com/flaky.json", "{\"nodes\":[{\"id\":2}]}");

        // Execute retry — LoadJsonPreviewAsync resets IsJsonOverlayError=false on success
        _vm.RetryJsonPreviewCommand.Execute(entry);

        // Wait for the fire-and-forget load to complete
        await Task.Delay(100);

        Assert.False(_vm.IsJsonOverlayError);
        Assert.False(_vm.IsJsonOverlayLoading);
        Assert.NotNull(_vm.JsonOverlayText);
        Assert.NotNull(entry.JsonPreview);   // now cached
        Assert.Contains("\"id\": 2", _vm.JsonOverlayText);
    }

    // v0.6.22 T3-R1: retry button CanExecute — disabled when entry has no WorkflowJsonUrl.
    [Fact]
    public void RetryJsonPreviewCommand_CanExecute_FalseWhenEntryHasNoJsonUrl()
    {
        var entry = new WorkflowEntry
        {
            Source = WorkflowSourceKind.CivitAi,
            SourceId = "no-url",
            Title = "No URL",
            WorkflowJsonUrl = null,
        };
        Assert.False(_vm.RetryJsonPreviewCommand.CanExecute(entry));
    }

    // v0.6.22 T3-R1: retry button CanExecute — false when param is not a WorkflowEntry.
    [Fact]
    public void RetryJsonPreviewCommand_CanExecute_FalseWhenParamNotEntry()
    {
        Assert.False(_vm.RetryJsonPreviewCommand.CanExecute("not-an-entry"));
        Assert.False(_vm.RetryJsonPreviewCommand.CanExecute(null));
    }

    private sealed class SlowMarketplaceService : WorkflowMarketplaceService
    {
        private readonly TaskCompletionSource<IReadOnlyList<WorkflowEntry>?> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SlowMarketplaceService(HttpClient? http = null) : base(Array.Empty<IWorkflowSource>(), logger: null, httpClient: http) { }
        public void Complete() => _tcs.SetResult(Array.Empty<WorkflowEntry>());
        public override Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
            => _tcs.Task!;
    }

    private sealed class StubMarketplaceService : WorkflowMarketplaceService
    {
        public IReadOnlyList<WorkflowEntry>? Next { get; set; }
        public Exception? ThrowOnNext { get; set; }

        public StubMarketplaceService(HttpClient? httpClient = null)
            : base(Array.Empty<IWorkflowSource>(), logger: null, httpClient: httpClient) { }

        public override Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
        {
            if (ThrowOnNext is not null) throw ThrowOnNext;
            return Task.FromResult(Next ?? Array.Empty<WorkflowEntry>());
        }
    }

    // v0.6.22 T3: DelegatingHandler that records requests + serves configured responses.
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _byUrl = new();
        private readonly Dictionary<string, string> _bodies = new();
        public int RequestCount { get; private set; }

        public void RegisterResponse(string url, string body)
            => _bodies[url] = body;
        public void RegisterStatus(string url, HttpStatusCode status)
            => _byUrl[url] = new HttpResponseMessage(status);
        public void ClearStatus(string url)
            => _byUrl.Remove(url);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            RequestCount++;
            var url = req.RequestUri?.ToString() ?? "";
            if (_byUrl.TryGetValue(url, out var resp)) return Task.FromResult(resp);
            if (_bodies.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            // default: 404
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}