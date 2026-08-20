using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelMarketplaceServiceTests
{
    [Fact]
    public async Task LoadAllAsync_TwoSources_NoOverlap_AggregatesAll()
    {
        var s1 = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "A" });
        var s2 = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "2", Title = "B" });

        var svc = new ModelMarketplaceService(new IModelSource[] { s1, s2 });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task LoadAllAsync_TwoSources_SameId_DedupsToOne()
    {
        var entry = new ModelEntry { Source = ModelSourceKind.CivitAi, SourceId = "1", Title = "A" };
        var s1 = new FakeSource(ModelSourceKind.CivitAi, entry);
        var s2 = new FakeSource(ModelSourceKind.CivitAi, entry);

        var svc = new ModelMarketplaceService(new IModelSource[] { s1, s2 });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
    }

    [Fact]
    public async Task LoadAllAsync_DisabledSource_Skipped()
    {
        var enabled = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "1", Title = "A" }) { IsEnabled = true };
        var disabled = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "2", Title = "B" }) { IsEnabled = false };

        var svc = new ModelMarketplaceService(new IModelSource[] { enabled, disabled });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
        Assert.Equal("1", result[0].SourceId);
    }

    [Fact]
    public async Task LoadAllAsync_OneSourceThrows_OthersStillReturn()
    {
        var good = new FakeSource(ModelSourceKind.CivitAi, new ModelEntry { SourceId = "1", Title = "A" }) { IsEnabled = true };
        var bad = new ThrowingSource { IsEnabled = true };

        var svc = new ModelMarketplaceService(new IModelSource[] { good, bad });
        var result = await svc.LoadAllAsync("", 50, default);

        Assert.Single(result);
        Assert.Equal("1", result[0].SourceId);
    }

    [Fact]
    public async Task LoadAllAsync_AllSourcesFail_ReturnsEmpty()
    {
        var svc = new ModelMarketplaceService(new IModelSource[] { new ThrowingSource(), new ThrowingSource() });
        var result = await svc.LoadAllAsync("", 50, default);
        Assert.Empty(result);
    }
}

internal class FakeSource : IModelSource
{
    private readonly ModelEntry[] _entries;
    public ModelSourceKind SourceKind { get; }
    public string DisplayName => "Fake";
    public bool IsEnabled { get; set; } = true;

    public FakeSource(ModelSourceKind kind, params ModelEntry[] entries)
    {
        SourceKind = kind;
        _entries = entries;
    }

    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelEntry>>(_entries);

    public Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<ModelEntry>, string?)>(
            ((IReadOnlyList<ModelEntry>)_entries, (string?)null));
}

internal class ThrowingSource : IModelSource
{
    public ModelSourceKind SourceKind => ModelSourceKind.CivitAi;
    public string DisplayName => "Throwing";
    public bool IsEnabled { get; set; } = true;
    public Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
    public Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}
