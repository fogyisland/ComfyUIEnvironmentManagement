using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

/// <summary>v1.0.0 T13-5:Orchestrator chains 4 IModelMatcher strategies in order;
/// first non-null wins. Tests use IReadOnlyList&lt;IModelMatcher&gt; ctor so we
/// can mock individual matchers without standing up real CivitAiLookupService.</summary>
public sealed class CivitaiMatcherOrchestratorTests
{
    private static DownloadedModel MakeModel() => new()
    {
        Title = "test",
        SubfolderName = "checkpoints",
        FullPath = "C:\\test.safetensors",
        Kind = ModelKind.Checkpoint,
        Source = "Local",
        SourceId = "local:test",
        SourceVersionId = "",
        DownloadedAt = DateTime.UtcNow,
        PreviewImagePath = null,
        Hash = "ABC",
    };

    private static MatchResult MakeResult(MatchSource src) => new(
        src,
        new CivitAiDetailDto(1, "Test", "u", null, "",
            Array.Empty<string>(),
            new List<CivitAiVersionDto>(),
            new List<string>()),
        null);

    [Fact]
    public async Task MatchAsync_HashHitWins_OtherMatchersNotCalled()
    {
        var hashMock = new Mock<IModelMatcher>();
        hashMock.SetupGet(m => m.Name).Returns("Hash");
        hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeResult(MatchSource.Hash));

        var metadataMock = new Mock<IModelMatcher>();
        metadataMock.SetupGet(m => m.Name).Returns("SafetensorsMetadata");
        metadataMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new Exception("should not be called"));

        var orch = new CivitaiMatcherOrchestrator(
            new IModelMatcher[] { hashMock.Object, metadataMock.Object });

        var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.Hash, result!.Source);
        metadataMock.Verify(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchAsync_HashMiss_FallsToMetadata()
    {
        var hashMock = new Mock<IModelMatcher>();
        hashMock.SetupGet(m => m.Name).Returns("Hash");
        hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MatchResult?)null);

        var metadataMock = new Mock<IModelMatcher>();
        metadataMock.SetupGet(m => m.Name).Returns("SafetensorsMetadata");
        metadataMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(MakeResult(MatchSource.SafetensorsMetadata));

        var orch = new CivitaiMatcherOrchestrator(
            new IModelMatcher[] { hashMock.Object, metadataMock.Object });

        var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(MatchSource.SafetensorsMetadata, result!.Source);
    }

    [Fact]
    public async Task MatchAsync_AllMatchersReturnNull_ReturnsNull()
    {
        var mocks = new[] { MatchSource.Hash, MatchSource.SafetensorsMetadata, MatchSource.CompanionJson, MatchSource.FilenameFuzzy }
        .Select(src =>
        {
            var m = new Mock<IModelMatcher>();
            m.SetupGet(x => x.Name).Returns(src.ToString());
            m.Setup(x => x.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((MatchResult?)null);
            return m.Object;
        }).ToArray();
        var orch = new CivitaiMatcherOrchestrator(mocks);
        var result = await orch.MatchAsync(MakeModel(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchAsync_CancellationPropagates()
    {
        var hashMock = new Mock<IModelMatcher>();
        hashMock.SetupGet(m => m.Name).Returns("Hash");
        hashMock.Setup(m => m.MatchAsync(It.IsAny<DownloadedModel>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
        var orch = new CivitaiMatcherOrchestrator(new IModelMatcher[] { hashMock.Object });
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => orch.MatchAsync(MakeModel(), CancellationToken.None));
    }
}