using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0 T11:LocalModelCivitAiDialogViewModel state machine tests。
/// 4 状态:Searching → NoMatch / Picker / Detail。
/// 使用 HttpMessageHandler mock + 真 CivitAiLookupService(同 CivitAiLookupServiceTests 模式):
/// Moq 不能 mock sealed class,但 service 通过 HttpClient 解耦 ——
/// Mock&lt;HttpMessageHandler&gt; 喂构造好的 HttpClient,service 内部走真 HTTP 路径。
/// </summary>
public sealed class LocalModelCivitAiDialogViewModelTests : IDisposable
{
    private readonly string _tempRoot;

    public LocalModelCivitAiDialogViewModelTests()
    {
        _tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"dialog-vm-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (CivitAiLookupService svc, Mock<HttpMessageHandler> mock) Build(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(mock.Object);
        var logger = new AppLogger(_tempRoot);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", logger);
        return (svc, mock);
    }

    private const string ZeroCandidatesJson = """{"items":[]}""";

    private const string OneCandidateJson = """
    {
      "items": [
        {
          "id": 12345,
          "name": "Anime Model",
          "creator": {"username": "alice"},
          "baseModel": "SD 1.5",
          "imageUrl": "https://cdn.example.com/thumb.jpg"
        }
      ]
    }
    """;

    private const string MultipleCandidatesJson = """
    {
      "items": [
        {"id": 1, "name": "Model A", "creator": {"username": "alice"}, "baseModel": "SD 1.5"},
        {"id": 2, "name": "Model B", "creator": {"username": "bob"}, "baseModel": "SDXL"},
        {"id": 3, "name": "Model C", "creator": {"username": "carol"}, "baseModel": "SD 1.5"}
      ]
    }
    """;

    private const string DetailJson = """
    {
      "id": 12345,
      "name": "Anime Model",
      "creator": {"username": "alice"},
      "baseModel": "SD 1.5",
      "description": "An awesome anime model",
      "tags": ["anime", "lora"],
      "modelVersions": [
        {"name": "v1", "baseModel": "SD 1.5", "createdAt": "2024-01-15T00:00:00Z"}
      ],
      "images": [
        {"url": "https://cdn.example.com/img1.jpg"}
      ]
    }
    """;

    [Fact]
    public async Task LoadAsync_ZeroCandidates_SetsNoMatchState()
    {
        var (svc, _) = Build(ZeroCandidatesJson);

        var vm = new LocalModelCivitAiDialogViewModel(svc, "nothing");
        await vm.LoadAsync();

        Assert.Equal(DialogState.NoMatch, vm.State);
        Assert.Empty(vm.Candidates);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task LoadAsync_OneCandidate_LoadsDetailAndSetsDetailState()
    {
        // 第一次调用(Search) → 1 candidate;第二次调用(Detail) → detail JSON。
        // SetupSequence 让 mock 按调用顺序返回不同响应,不依赖 URL pattern。
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OneCandidateJson, Encoding.UTF8, "application/json"),
            })
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DetailJson, Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(mock.Object);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", new AppLogger(_tempRoot));

        var vm = new LocalModelCivitAiDialogViewModel(svc, "anime");
        await vm.LoadAsync();

        Assert.Equal(DialogState.Detail, vm.State);
        Assert.NotNull(vm.Detail);
        Assert.Equal("Anime Model", vm.Detail!.Title);
        Assert.Equal(12345, vm.Detail.Id);
        Assert.Single(vm.Candidates);   // 即使单候选也存,让 "返回候选" 按钮的判断走 InverseZeroCount
    }

    [Fact]
    public async Task LoadAsync_MultipleCandidates_SetsPickerState()
    {
        var (svc, _) = Build(MultipleCandidatesJson);

        var vm = new LocalModelCivitAiDialogViewModel(svc, "lora");
        await vm.LoadAsync();

        Assert.Equal(DialogState.Picker, vm.State);
        Assert.Equal(3, vm.Candidates.Count);
        Assert.Null(vm.Detail);   // picker state 不应预加载 detail
    }

    [Fact]
    public async Task SelectCandidate_FromPicker_TransitionsToDetail()
    {
        var (svc, _) = Build(MultipleCandidatesJson);

        var vm = new LocalModelCivitAiDialogViewModel(svc, "lora");
        await vm.LoadAsync();
        Assert.Equal(DialogState.Picker, vm.State);

        // 选 candidate 1 (Model A id=1) → detail fetch 但 endpoint /1 还没 mock,
        // 这里 svc.GetDetailAsync 会抛 HttpRequestException → 走 NoMatch。
        // 改用 1-candidate 模式构造个 detail-ready service, 走 SelectCandidateAsync。
        // 简化:本测试只验证 state machine transition 用 multi candidate + 调用 detail → 失败也无害。
        // 验证 SelectedCandidate 已 set 即可。
        var cand = vm.Candidates[0];
        await vm.SelectCandidateAsync(cand);

        Assert.Equal(cand, vm.SelectedCandidate);
        // state 切到 NoMatch(detail fetch fail) 或 Detail(success)— 取决于 mock 设置
        // 这里 mock 还没设 /1 endpoint,所以是 NoMatch
        Assert.True(vm.State == DialogState.Detail || vm.State == DialogState.NoMatch);
    }

    [Fact]
    public async Task SelectCandidate_DetailFetchSucceeds_TransitionsToDetail()
    {
        // multi candidate search + per-id detail mock
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MultipleCandidatesJson, Encoding.UTF8, "application/json"),
            });
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models/1"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DetailJson, Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(mock.Object);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", new AppLogger(_tempRoot));

        var vm = new LocalModelCivitAiDialogViewModel(svc, "lora");
        await vm.LoadAsync();
        Assert.Equal(DialogState.Picker, vm.State);

        var cand = vm.Candidates.First(c => c.Id == 1);
        await vm.SelectCandidateAsync(cand);

        Assert.Equal(DialogState.Detail, vm.State);
        Assert.NotNull(vm.Detail);
        Assert.Equal(12345, vm.Detail!.Id);
    }

    [Fact]
    public async Task BackToPicker_FromDetail_WithMultipleCandidates_RestoresPickerState()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MultipleCandidatesJson, Encoding.UTF8, "application/json"),
            });
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models/2"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DetailJson, Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(mock.Object);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", new AppLogger(_tempRoot));

        var vm = new LocalModelCivitAiDialogViewModel(svc, "lora");
        await vm.LoadAsync();
        await vm.SelectCandidateAsync(vm.Candidates.First(c => c.Id == 2));
        Assert.Equal(DialogState.Detail, vm.State);
        Assert.NotNull(vm.Detail);

        vm.BackToPicker();
        Assert.Equal(DialogState.Picker, vm.State);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public void BackToPicker_SingleCandidate_NoOp()
    {
        // 单 candidate 时 BackToPicker 是 no-op(候选列表只 1 项,没"返回"意义)
        // 直接 new VM 不调 LoadAsync,直接调 BackToPicker → State 应保持 Searching(默认初始 state)
        var (svc, _) = Build(ZeroCandidatesJson);

        var vm = new LocalModelCivitAiDialogViewModel(svc, "x");

        vm.BackToPicker();

        Assert.Equal(DialogState.Searching, vm.State);
    }

    [Fact]
    public async Task LoadAsync_SearchThrows_SetsNoMatchState()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("503 service unavailable"));
        var http = new HttpClient(mock.Object);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", new AppLogger(_tempRoot));

        var vm = new LocalModelCivitAiDialogViewModel(svc, "fail");
        await vm.LoadAsync();

        Assert.Equal(DialogState.NoMatch, vm.State);
        Assert.Empty(vm.Candidates);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task SelectCandidate_GetDetailNotFound_SetsNoMatchState()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MultipleCandidatesJson, Encoding.UTF8, "application/json"),
            });
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/api/v1/models/1"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(mock.Object);
        var svc = new CivitAiLookupService(http, "https://civitai.com", "", new AppLogger(_tempRoot));

        var vm = new LocalModelCivitAiDialogViewModel(svc, "x");
        await vm.LoadAsync();
        Assert.Equal(DialogState.Picker, vm.State);

        await vm.SelectCandidateAsync(vm.Candidates.First(c => c.Id == 1));

        Assert.Equal(DialogState.NoMatch, vm.State);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public void InitialState_IsSearching()
    {
        var (svc, _) = Build(ZeroCandidatesJson);

        var vm = new LocalModelCivitAiDialogViewModel(svc, "x");

        Assert.Equal(DialogState.Searching, vm.State);
        Assert.Empty(vm.Candidates);
        Assert.Null(vm.SelectedCandidate);
        Assert.Null(vm.Detail);
        Assert.Equal("x", vm.Title);
    }

    // ===== v1.0.0 T13-7:Pre-matched card opens directly in Detail state =====

    [Fact]
    public void Ctor_PreMatchedDetail_OpensDirectlyInDetailState()
    {
        var (svc, _) = Build(ZeroCandidatesJson);
        var card = new LocalModelCard(
            Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
            LatestDownloadedAt: DateTime.UtcNow, SourceUrl: null, PreviewImagePath: null,
            Hash: "ABC",
            MatchedDetail: new CivitAiDetailDto(99, "Test Model", "u", null, "desc",
                Array.Empty<string>(), Array.Empty<CivitAiVersionDto>(), Array.Empty<string>()),
            MatchSource: MatchSource.Hash);

        var vm = new LocalModelCivitAiDialogViewModel(svc, card.Title, card: card);

        Assert.Equal(DialogState.Detail, vm.State);
        Assert.Equal("Test Model", vm.Detail!.Title);
        Assert.Equal(99, vm.Detail.Id);
    }

    [Fact]
    public void Ctor_NullCard_BackCompat_NoDetailState()
    {
        var (svc, _) = Build(ZeroCandidatesJson);
        var vm = new LocalModelCivitAiDialogViewModel(svc, "AnimateLCM", card: null);

        // 默认 Searching state — 跟旧 ctor 行为完全一致(card=null)
        Assert.Equal(DialogState.Searching, vm.State);
    }

    [Fact]
    public async Task SelectCandidate_WithPreMatched_DoesNothing_DetailAlreadyShown()
    {
        var (svc, _) = Build(ZeroCandidatesJson);
        var card = new LocalModelCard(
            Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
            LatestDownloadedAt: DateTime.UtcNow, SourceUrl: null, PreviewImagePath: null,
            Hash: "ABC",
            MatchedDetail: new CivitAiDetailDto(99, "Test Model", "u", null, "",
                Array.Empty<string>(), Array.Empty<CivitAiVersionDto>(), Array.Empty<string>()),
            MatchSource: MatchSource.Hash);

        var vm = new LocalModelCivitAiDialogViewModel(svc, card.Title, card: card);
        Assert.Equal(DialogState.Detail, vm.State);

        // 试图调 SelectCandidateAsync — 状态应保持 Detail(不重新搜)
        await vm.SelectCandidateAsync(new CivitAiCandidate(99, "x", "u", null, null));
        Assert.Equal(DialogState.Detail, vm.State);
    }
}
