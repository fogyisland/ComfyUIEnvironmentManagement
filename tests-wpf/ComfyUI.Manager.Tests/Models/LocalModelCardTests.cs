using System;
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

    // v1.0.0.x (2026-09-17) T46i.2:DisplayTitle 派生属性 — 优先 CivitAI title,有则用,无则 fallback scanner Title。

    [Fact]
    public void DisplayTitle_NoMatchedDetail_FallsBackToScannerTitle()
    {
        var card = new LocalModelCard(
            SourceId: "x", Title: "Flux1 Dev Fp8", Kind: ModelKind.Other,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: null, MatchSource: null);
        Assert.Equal("Flux1 Dev Fp8", card.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_WithCivitAiTitle_UsesCivitAi()
    {
        var detail = new CivitAiDetailDto(
            Id: 694648, Title: "DEV FP8 - Kijai [11 GB]", Username: "",
            BaseModel: "Flux.1 D", Description: "", Tags: Array.Empty<string>(),
            Versions: Array.Empty<CivitAiVersionDto>(), ImageUrls: Array.Empty<string>());
        var card = new LocalModelCard(
            SourceId: "x", Title: "Flux1 Dev Fp8", Kind: ModelKind.Other,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: "ABC", MatchedDetail: detail, MatchSource: MatchSource.Hash);
        Assert.Equal("DEV FP8 - Kijai [11 GB]", card.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_CivitAiTitleEmptyOrWhitespace_FallsBackToScannerTitle()
    {
        var detailEmpty = new CivitAiDetailDto(
            Id: 1, Title: "", Username: "", BaseModel: null, Description: "",
            Tags: Array.Empty<string>(), Versions: Array.Empty<CivitAiVersionDto>(),
            ImageUrls: Array.Empty<string>());
        var detailWs = new CivitAiDetailDto(
            Id: 1, Title: "   ", Username: "", BaseModel: null, Description: "",
            Tags: Array.Empty<string>(), Versions: Array.Empty<CivitAiVersionDto>(),
            ImageUrls: Array.Empty<string>());
        var cardEmpty = new LocalModelCard(
            SourceId: "x", Title: "scanner-name", Kind: ModelKind.LORA,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: detailEmpty, MatchSource: MatchSource.UserQuery);
        Assert.Equal("scanner-name", cardEmpty.DisplayTitle);
        var cardWs = new LocalModelCard(
            SourceId: "x", Title: "scanner-name", Kind: ModelKind.LORA,
            Source: "Local", VersionCount: 1, LatestDownloadedAt: null,
            SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: detailWs, MatchSource: MatchSource.UserQuery);
        Assert.Equal("scanner-name", cardWs.DisplayTitle);
    }
}