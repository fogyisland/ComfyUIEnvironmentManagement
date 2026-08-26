using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v1.0.0.x: LocalModelCard 是 positional record,带默认 <see cref="LocalModelCard.LocalPathOverride"/>
/// 字段 + <see cref="LocalModelCard.WithLocalPathOverride"/> 不可变更新方法。
/// </summary>
public class LocalModelCardTests
{
    [Fact]
    public void LocalPathOverride_DefaultsToNull()
    {
        var card = new LocalModelCard(
            SourceId: "x", Title: "T", Kind: ModelKind.Checkpoint,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: null, MatchSource: null);
        Assert.Null(card.LocalPathOverride);
    }

    [Fact]
    public void WithLocalPathOverride_SetsPath()
    {
        var card = new LocalModelCard(
            SourceId: "x", Title: "T", Kind: ModelKind.Checkpoint,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: null, MatchSource: null);
        var updated = card.WithLocalPathOverride(@"D:\override\model.safetensors");
        Assert.Equal(@"D:\override\model.safetensors", updated.LocalPathOverride);
        // 其他字段不变(不可变 with)
        Assert.Equal(card.Title, updated.Title);
        Assert.Equal(card.Kind, updated.Kind);
        Assert.Equal(card.Source, updated.Source);
        Assert.Equal(card.SourceId, updated.SourceId);
    }

    [Fact]
    public void WithLocalPathOverride_EmptyOrNull_ClearsToNull()
    {
        var card = new LocalModelCard(
            SourceId: "x", Title: "T", Kind: ModelKind.Checkpoint,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: null, MatchSource: null,
            LocalPathOverride: @"D:\initial");
        Assert.NotNull(card.LocalPathOverride);

        var cleared1 = card.WithLocalPathOverride("");
        Assert.Null(cleared1.LocalPathOverride);

        var cleared2 = card.WithLocalPathOverride(null);
        Assert.Null(cleared2.LocalPathOverride);
    }
}