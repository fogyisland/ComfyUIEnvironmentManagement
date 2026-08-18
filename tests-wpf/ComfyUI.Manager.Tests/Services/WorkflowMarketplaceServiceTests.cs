using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowMarketplaceServiceTests
{
    private sealed class StubSource : IWorkflowSource
    {
        public WorkflowSourceKind SourceKind { get; set; }
        public string DisplayName => SourceKind.ToString();
        public bool IsEnabled { get; set; } = true;
        public Func<string, IReadOnlyList<WorkflowEntry>>? Handler { get; set; }
        public Task<IReadOnlyList<WorkflowEntry>> SearchAsync(string q, int n, CancellationToken ct = default)
            => Task.FromResult(Handler?.Invoke(q) ?? Array.Empty<WorkflowEntry>());
    }

    private static WorkflowEntry Entry(WorkflowSourceKind src, string id, string title = "t")
        => new() { Source = src, SourceId = id, SourceUrl = $"https://{src}/{id}",
                   WorkflowJsonUrl = $"https://{src}/{id}.json", Title = title };

    [Fact]
    public async Task LoadAllAsync_3Sources_AggregatesAll()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
            new StubSource { SourceKind = WorkflowSourceKind.OpenArt,
                Handler = _ => new[] { Entry(WorkflowSourceKind.OpenArt, "c") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task LoadAllAsync_DedupBy_SourceAndSourceId()
    {
        // Same (Source, id) from 2 sources → 1 entry(罕见 cross-source id 冲突,假设不发生;
        // dedup 主要防同一 source 跨 batch 重复)
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "dup") } },
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "dup") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_DisabledSource_Skipped()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson, IsEnabled = false,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi, IsEnabled = true,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
    }

    [Fact]
    public async Task LoadAllAsync_OneSourceThrows_OthersStillReturned()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { SourceKind = WorkflowSourceKind.CommunityJson,
                Handler = _ => throw new InvalidOperationException("boom") },
            new StubSource { SourceKind = WorkflowSourceKind.CivitAi,
                Handler = _ => new[] { Entry(WorkflowSourceKind.CivitAi, "b") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_AllSourcesDisabled_ReturnsEmpty()
    {
        var svc = new WorkflowMarketplaceService(new[]
        {
            new StubSource { IsEnabled = false, Handler = _ => new[] { Entry(WorkflowSourceKind.CommunityJson, "a") } },
        });

        var result = await svc.LoadAllAsync(query: "", maxResultsPerSource: 10);

        Assert.Empty(result);
    }
}