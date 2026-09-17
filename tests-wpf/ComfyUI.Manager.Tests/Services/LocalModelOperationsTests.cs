using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.Civitai;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:LocalModelOperations 7 命令 + 3 测试 seam 验证。
///
/// Pre-flight deviations (documented in task-3-report.md):
/// - 17 tests adapted from brief §Step 1 code:
///   * Brief uses NSubstitute + Substitute.For<ModelHasher> + Substitute.For<CivitaiMatcherOrchestrator>
///     — both impossible because ModelHasher is static + CivitaiMatcherOrchestrator.MatchAsync is
///     non-virtual on sealed class. Constructor signature changed to Func-based injection
///     (Func<string, CancellationToken, string> for hash; Func<DownloadedModel,
///     CancellationToken, Task<MatchResult?>> for match lookup). Production wire passes
///     ModelHasher.ComputeTensorOnlySha256 and orchestrator.MatchAsync as method groups.
///   * Tests use Moq (project convention per csproj) not NSubstitute.
///   * LocalModelCard.SourcePath added to record (brief decision (a)) — tests use it directly.
///   * LocalModelCard 暂无 IsStarred field — tests verify Star repo state only, not card property.
/// </summary>
public sealed class LocalModelOperationsTests : IDisposable
{
    private readonly ToastNotification _toast;
    private readonly StarredModelsRepository _stars;
    private readonly Mock<IProgressDialogService> _progress;
    private readonly LocalModelOperations _ops;

    public LocalModelOperationsTests()
    {
        using var db = new TestDb();

        _toast = new ToastNotification();
        _stars = new StarredModelsRepository(db.ModelFactory);
        _progress = new Mock<IProgressDialogService>();

        // 测试 seam:progress.ShowIndeterminate 返回 no-op disposable — using 块正常 dispose
        _progress.Setup(p => p.ShowIndeterminate(It.IsAny<string>()))
                 .Returns(new NoopDisposable());

        // 默认 hash/match funcs 是 no-op lambda — 各测试按需 override _ops 字段不直接构造 ops
        Func<string, CancellationToken, string> hashFunc = (_, _) => "DEADHASH";
        Func<DownloadedModel, CancellationToken, Task<MatchResult?>> matchFunc =
            (_, _) => Task.FromResult<MatchResult?>(null);

        _ops = new LocalModelOperations(_toast, _stars, hashFunc, matchFunc, _progress.Object);
    }

    public void Dispose() { /* no-op, _stars is on TestDb which is disposed per-test via 'using' */ }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    private static LocalModelOperations BuildOps(
        ToastNotification toast,
        StarredModelsRepository stars,
        Func<string, CancellationToken, string> hashFunc,
        Func<DownloadedModel, CancellationToken, Task<MatchResult?>> matchFunc,
        IProgressDialogService progress)
        => new LocalModelOperations(toast, stars, hashFunc, matchFunc, progress);

    /// <summary>LocalModelCard 测试 fixture 工厂 —— SourcePath 默认 @"D:\m.safetensors"。</summary>
    private static LocalModelCard MakeCard(
        string path = @"D:\m.safetensors",
        string? hash = null,
        CivitAiDetailDto? detail = null)
        => new(
            SourceId: "x",
            Title: "T",
            Kind: ModelKind.Checkpoint,
            Source: "Local",
            VersionCount: 1,
            LatestDownloadedAt: null,
            SourceUrl: null,
            PreviewImagePath: null,
            Hash: hash,
            MatchedDetail: detail,
            MatchSource: null,
            LocalPathOverride: null,
            SourcePath: path);

    // ===================== ToggleStar =====================

    [Fact]
    public void ToggleStar_NotStarred_AddsToStarsRepo()
    {
        var card = MakeCard(path: @"D:\star_test_new.safetensors");
        var result = _ops.ToggleStar(card);
        Assert.True(_stars.IsStarred(card.SourcePath));
        Assert.Same(card, result);  // T3 返回原 card(IsStarred 派生 T4 才加)
    }

    [Fact]
    public void ToggleStar_AlreadyStarred_RemovesFromStarsRepo()
    {
        var card = MakeCard(path: @"D:\star_test_existing.safetensors");
        _stars.Add(card.SourcePath);
        var result = _ops.ToggleStar(card);
        Assert.False(_stars.IsStarred(card.SourcePath));
        Assert.Same(card, result);
    }

    // ===================== CopyHash =====================

    [Fact]
    public void CopyHash_WithHash_SetsClipboardAndNoThrow()
    {
        string? captured = null;
        _ops.SetClipboardTextOverride = t => captured = t;
        _ops.CopyHash(MakeCard(hash: "ABC123"));
        Assert.Equal("ABC123", captured);
    }

    [Fact]
    public void CopyHash_NoHash_DoesNotInvokeClipboard()
    {
        bool called = false;
        _ops.SetClipboardTextOverride = _ => called = true;
        _ops.CopyHash(MakeCard(hash: null));
        Assert.False(called);  // toast.Warning 早返回,不调 seam
    }

    // ===================== OpenFolder =====================

    [Fact]
    public void OpenFolder_ValidPath_InvokesExplorerOverride()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"lmo_open_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, "x");
        try
        {
            string? openedPath = null;
            _ops.OpenInExplorerOverride = p => openedPath = p;
            _ops.OpenFolder(MakeCard(path: tmp));
            Assert.Equal(tmp, openedPath);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* teardown best-effort */ }
        }
    }

    [Fact]
    public void OpenFolder_MissingPath_DoesNotInvokeOverride()
    {
        string? openedPath = null;
        _ops.OpenInExplorerOverride = p => openedPath = p;
        _ops.OpenFolder(MakeCard(path: @"D:\never_existed_lmo_test.safetensors"));
        Assert.Null(openedPath);  // File.Exists false → toast.Warning 早返回
    }

    // ===================== ReloadHash =====================

    [Fact]
    public async Task ReloadHash_HashFuncCalled_WithCardSourcePath()
    {
        string? calledPath = null;
        var ops = BuildOps(_toast, _stars,
            (p, _) => { calledPath = p; return "NEWHASH"; },
            (_, _) => Task.FromResult<MatchResult?>(null),
            _progress.Object);

        var card = MakeCard(path: @"D:\reload_hash.safetensors");
        await ops.ReloadHashAsync(card, CancellationToken.None);

        Assert.Equal(@"D:\reload_hash.safetensors", calledPath);
        _progress.Verify(p => p.ShowIndeterminate(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ReloadHash_HashFuncThrows_DoesNotRethrow()
    {
        var ops = BuildOps(_toast, _stars,
            (_, _) => throw new IOException("disk error"),
            (_, _) => Task.FromResult<MatchResult?>(null),
            _progress.Object);
        var card = MakeCard(path: @"D:\reload_hash_err.safetensors");
        await ops.ReloadHashAsync(card, CancellationToken.None);  // 不抛
    }

    // ===================== RefreshMetadata =====================

    [Fact]
    public async Task RefreshMetadata_MatchFuncCalled_WithProbeDownloadedModel()
    {
        DownloadedModel? probed = null;
        var ops = BuildOps(_toast, _stars,
            (_, _) => "HASH",
            (m, _) => { probed = m; return Task.FromResult<MatchResult?>(null); },
            _progress.Object);

        var card = MakeCard(path: @"D:\refresh_metadata.safetensors", hash: "ABC");
        await ops.RefreshMetadataAsync(card, CancellationToken.None);

        Assert.NotNull(probed);
        Assert.Equal(card.SourcePath, probed!.FullPath);
        Assert.Equal("ABC", probed.Hash);
        Assert.Equal(card.SourceId, probed.SourceId);
    }

    [Fact]
    public async Task RefreshMetadata_MatchFuncReturnsNull_NoRethrow()
    {
        var ops = BuildOps(_toast, _stars,
            (_, _) => "HASH",
            (_, _) => Task.FromResult<MatchResult?>(null),
            _progress.Object);
        var card = MakeCard(hash: "ABC");
        await ops.RefreshMetadataAsync(card, CancellationToken.None);  // 不抛
    }

    [Fact]
    public async Task RefreshMetadata_MatchFuncThrows_DoesNotRethrow()
    {
        var ops = BuildOps(_toast, _stars,
            (_, _) => "HASH",
            (_, _) => throw new HttpRequestException("API down"),
            _progress.Object);
        var card = MakeCard(hash: "ABC");
        await ops.RefreshMetadataAsync(card, CancellationToken.None);  // 不抛
    }

    // ===================== CopySourceUrl =====================

    [Fact]
    public void CopySourceUrl_WithDetail_CopiesFullUrl()
    {
        var detail = new CivitAiDetailDto(
            Id: 694648,
            Title: "X",
            Username: "",
            BaseModel: null,
            Description: "",
            Tags: Array.Empty<string>(),
            Versions: Array.Empty<CivitAiVersionDto>(),
            ImageUrls: Array.Empty<string>());
        string? captured = null;
        _ops.SetClipboardTextOverride = t => captured = t;
        _ops.CopySourceUrl(MakeCard(detail: detail));
        Assert.Equal("https://civitai.com/models/694648", captured);
    }

    [Fact]
    public void CopySourceUrl_NoDetail_DoesNotInvokeClipboard()
    {
        bool called = false;
        _ops.SetClipboardTextOverride = _ => called = true;
        _ops.CopySourceUrl(MakeCard(detail: null));
        Assert.False(called);
    }

    // ===================== CopyFileName =====================

    [Fact]
    public void CopyFileName_StripsPath()
    {
        string? captured = null;
        _ops.SetClipboardTextOverride = t => captured = t;
        _ops.CopyFileName(MakeCard(path: @"D:\models\copy_filename.safetensors"));
        Assert.Equal("copy_filename.safetensors", captured);
    }

    // ===================== Delete =====================

    [Fact]
    public void Delete_UserClicksNo_NoOp()
    {
        _ops.ShowConfirmDialogOverride = (_, _, _, _) => System.Windows.MessageBoxResult.No;
        // 文件不存在也无所谓 — 用户点 No → 早返回
        _ops.Delete(MakeCard(path: @"D:\never_touched_lmo.safetensors"));
        // 无异常即通过
    }

    [Fact]
    public void Delete_UserClicksYes_FileNotExists_DoesNotThrow()
    {
        _ops.ShowConfirmDialogOverride = (_, _, _, _) => System.Windows.MessageBoxResult.Yes;
        // 文件不存在 → 走 "文件已不存在,仅清理数据库" 分支 → info toast,not throw
        _ops.Delete(MakeCard(path: @"D:\truly_nonexistent_lmo.safetensors"));
    }

    [Fact]
    public void Delete_DoesNotThrow_OnOverrideException()
    {
        _ops.ShowConfirmDialogOverride = (_, _, _, _) => throw new InvalidOperationException("test dialog crash");
        _ops.Delete(MakeCard());  // seam 抛 → caught → toast error,not throw
    }
}
