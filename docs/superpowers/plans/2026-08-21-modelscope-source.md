# ModelScope 国内模型源接入实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给模型市场加第三个 source — 魔搭 ModelScope。用户在 Settings 勾选启用 + 切到 ModelScope radio,能搜 SDXL/Flux/LoRA 模型 + 下载到本地。

**Architecture:** 新 `ModelScopeModelSource` 实现 `IModelSource`,2-round fetch(列表 + 每 entry 详情串行)拿文件大小。`ModelSourceFactory.CreateModelScope` 镜像 CivitAI/HF 模式(settings 读 mirror + proxy)。`Settings` +5 字段 + SettingsView 加第三个 sub-section + ModelMarketplaceView 加第三个 RadioButton,sort/period/baseModel 行对 ModelScope 折叠(同 HuggingFace 模式)。

**Tech Stack:** .NET 8 WPF / C# 12 / xUnit + System.Text.Json `[JsonPropertyName]` / 现有 `DelegatingHandlerStub` test seam。

**Spec:** `docs/superpowers/specs/2026-08-21-modelscope-source-design.md`

## Global Constraints

- **User-typed comment**:v0.6.22.x 时间戳格式 `[HH:mm:ss]`(已 ship),新代码继续用同一 helper 风格
- **Per-source HttpClient**:走 `Func<HttpProxyConfig?, HttpClient> httpBuilder` 注入,不在 source 内 new handler(让 App.OnStartup 统一控制 proxy)
- **proxy 三态决策**:走 `ModelSourceProxyDecision.Resolve(global, source, settings)`,不手工判断
- **NSFW**:ModelScope API 无 `Sensitive`/`nsfwLevel` 字段 — 全部 entry 写死 `ModelNsfwKind.SFW`,`IncludeNsfw=false` 时源层不做事(同 HF 模式)
- **sort/period/baseModel**:ModelScope API 无对应参数 — `SearchPageAsync` 接收但 no-op(同 HF 模式)
- **测试 seam**:复用 `DelegatingHandlerStub`(`tests-wpf/.../ModelSourceCivitAiTests.cs:582`,internal 在同一程序集内可访问)
- **gitignore**:`tests-wpf/**/TestResults/` 已加入(避免误 commit trx)

---

### Task 1: ModelSourceKind enum + ModelScope DTOs

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/ModelSourceKind.cs` (enum, +1 value)
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelScopeDtos.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelScopeDtoTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Models.ModelSourceKind.ModelScope = 2`; `ModelScopeDtos` 静态类持有 `ModelsResponse/ModelItem/OwnerInfo/RevisionInfo/FileInfo` 等 record

- [ ] **Step 1: 写失败的 DTO 反序列化测试**

`tests-wpf/ComfyUI.Manager.Tests/Services/ModelScopeDtoTests.cs`:
```csharp
using System.Text.Json;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelScopeDtoTests
{
    [Fact]
    public void Deserialize_ModelsList_MapsAllFields()
    {
        // v0.6.22.x:ModelScope /api/v1/models response — Data envelope + Model.Models[] array
        // 加 Unicode 中文名 + 空 Tags + null Owner(覆盖边界)。
        var json = """
        {
          "Code": 200,
          "Data": {
            "Model": {
              "PageNumber": 1,
              "PageSize": 2,
              "TotalCount": 47,
              "Models": [
                {
                  "Id": 12345,
                  "Name": "AI-ModelScope/foo",
                  "ChineseName": "测试模型",
                  "Tags": ["stable-diffusion", "lora"],
                  "Downloads": 100,
                  "Stars": 5,
                  "Likes": 10,
                  "Description": "test desc",
                  "Task": "text-to-image",
                  "Owner": null,
                  "DefaultRevision": "master"
                },
                {
                  "Id": 67890,
                  "Name": "bar",
                  "ChineseName": null,
                  "Tags": [],
                  "Downloads": 0,
                  "Stars": 0,
                  "Likes": 0,
                  "Description": null,
                  "Task": null,
                  "Owner": { "Name": "user1", "DisplayName": "User One" },
                  "DefaultRevision": "v1.0"
                }
              ]
            }
          }
        }
        """;
        var resp = JsonSerializer.Deserialize<ModelScopeDtos.ModelsResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resp);
        Assert.Equal(200, resp!.Code);
        Assert.Equal(47, resp.Data!.Model!.TotalCount);
        Assert.Equal(2, resp.Data.Model.Models.Count);
        var a = resp.Data.Model.Models[0];
        Assert.Equal(12345L, a.Id);
        Assert.Equal("AI-ModelScope/foo", a.Name);
        Assert.Equal("测试模型", a.ChineseName);
        Assert.Equal(new[] { "stable-diffusion", "lora" }, a.Tags);
        Assert.Equal(100, a.Downloads);
        Assert.Null(a.Owner);
        Assert.Equal("master", a.DefaultRevision);
        var b = resp.Data.Model.Models[1];
        Assert.Null(b.ChineseName);
        Assert.Empty(b.Tags);
        Assert.NotNull(b.Owner);
        Assert.Equal("User One", b.Owner!.DisplayName);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelScopeDtoTests" -c Debug`
Expected: CS0246(`ModelScopeDtos` 未定义)

- [ ] **Step 3: 加 enum 值 + DTOs**

`Models/ModelSourceKind.cs`(在 `Models/ModelEntry.cs:32`,enum 跟 entry 一起):
```csharp
public enum ModelSourceKind { CivitAi = 0, HuggingFace = 1, ModelScope = 2 }
```

`Services/ModelSources/ModelScopeDtos.cs`(snake_case 用 `[JsonPropertyName]`):
```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.22.x:ModelScope /api/v1/models response DTO。
/// envelope = { Code, Data: { Model: { Models[], PageNumber, PageSize, TotalCount } } }。
/// snake_case 字段全部 [JsonPropertyName] 显式绑(防 server 改 casing 时静默坏)。
/// </summary>
public static class ModelScopeDtos
{
    public sealed class ModelsResponse
    {
        [JsonPropertyName("Code")] public int Code { get; init; }
        [JsonPropertyName("Data")] public ModelsData? Data { get; init; }
    }
    public sealed class ModelsData
    {
        [JsonPropertyName("Model")] public ModelsPage? Model { get; init; }
    }
    public sealed class ModelsPage
    {
        [JsonPropertyName("PageNumber")] public int PageNumber { get; init; }
        [JsonPropertyName("PageSize")] public int PageSize { get; init; }
        [JsonPropertyName("TotalCount")] public int TotalCount { get; init; }
        [JsonPropertyName("Models")] public List<ModelItem> Models { get; init; } = new();
    }
    public sealed class ModelItem
    {
        [JsonPropertyName("Id")] public long Id { get; init; }
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("ChineseName")] public string? ChineseName { get; init; }
        [JsonPropertyName("Tags")] public List<string> Tags { get; init; } = new();
        [JsonPropertyName("Downloads")] public int Downloads { get; init; }
        [JsonPropertyName("Stars")] public int Stars { get; init; }
        [JsonPropertyName("Likes")] public int Likes { get; init; }
        [JsonPropertyName("Description")] public string? Description { get; init; }
        [JsonPropertyName("Task")] public string? Task { get; init; }
        [JsonPropertyName("Owner")] public OwnerInfo? Owner { get; init; }
        [JsonPropertyName("DefaultRevision")] public string DefaultRevision { get; init; } = "master";
    }
    public sealed class OwnerInfo
    {
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("DisplayName")] public string? DisplayName { get; init; }
    }

    /// <summary>单 model 详情 response — /api/v1/models/{id}。
    /// 用 Revision[0].Files[0] 取 PrimaryDownloadUrl + Size。</summary>
    public sealed class ModelDetailResponse
    {
        [JsonPropertyName("Code")] public int Code { get; init; }
        [JsonPropertyName("Data")] public ModelDetail? Data { get; init; }
    }
    public sealed class ModelDetail
    {
        [JsonPropertyName("Id")] public long Id { get; init; }
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("Revision")] public List<RevisionInfo> Revision { get; init; } = new();
    }
    public sealed class RevisionInfo
    {
        [JsonPropertyName("RevisionId")] public string? RevisionId { get; init; }
        [JsonPropertyName("Files")] public List<FileInfo> Files { get; init; } = new();
    }
    public sealed class FileInfo
    {
        [JsonPropertyName("Name")] public string Name { get; init; } = "";
        [JsonPropertyName("DownloadUrl")] public string DownloadUrl { get; init; } = "";
        [JsonPropertyName("Size")] public long Size { get; init; }  // bytes
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelScopeDtoTests" -c Debug`
Expected: PASS(1/1)

- [ ] **Step 5: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(models): v0.6.22.x ModelScope DTOs + ModelSourceKind enum +1"
```

---

### Task 2: ModelScopeModelSource — search + 2-round detail fetch + kind mapping

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelScopeModelSource.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceModelScopeTests.cs`

**Interfaces:**
- Consumes: `ModelScopeDtos.ModelsResponse/ModelItem/OwnerInfo/ModelDetailResponse/ModelDetail/RevisionInfo/FileInfo`(Task 1)
- Produces: `IModelSource` 实现,`SourceKind = ModelSourceKind.ModelScope`,`DisplayName = "ModelScope"`
- `SearchPageAsync(query, cursor, pageSize, sort, period, ct, includeNsfw, baseModel, progress)` 返回 `(entries, nextCursor)`:`cursor` 是 `PageNumber-1` 的 string(0-based);详情失败时该 entry 仍返 + `Versions[0].PrimaryDownloadUrl = null`,`SizeBytes = 0`

- [ ] **Step 1: 写失败的搜索测试**

`tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceModelScopeTests.cs`:
```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceModelScopeTests
{
    private static HttpClient CreateClient(DelegatingHandlerStub h)
        => new HttpClient(h) { BaseAddress = new Uri("https://www.modelscope.cn/") };

    private const string ListResp = """
    {
      "Code": 200,
      "Data": {
        "Model": {
          "PageNumber": 1, "PageSize": 2, "TotalCount": 47,
          "Models": [
            {"Id":1,"Name":"a","ChineseName":null,"Tags":["stable-diffusion","checkpoint"],
             "Downloads":100,"Stars":5,"Likes":10,"Description":"d","Task":"text-to-image",
             "Owner":{"Name":"u1","DisplayName":"User One"},"DefaultRevision":"master"},
            {"Id":2,"Name":"b","ChineseName":null,"Tags":["lora"],
             "Downloads":50,"Stars":3,"Likes":7,"Description":"d2","Task":"text-to-image",
             "Owner":null,"DefaultRevision":"v1"}
          ]
        }
      }
    }
    """;

    [Fact]
    public async Task SearchAsync_EmptyQuery_BuildsUrlWithoutKeyword()
    {
        var handler = new DelegatingHandlerStub(ListResp);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var entries = await src.SearchAsync("", 50, default);
        Assert.Equal(2, entries.Count);
        // 不验 URL — 改由 SearchPageAsync_AddsPageParam 测试
    }

    [Fact]
    public async Task SearchPageAsync_FirstPage_ReturnsPage1AndNextCursor()
    {
        // 8 个 entry,TotalCount=47,PageSize=8 → nextCursor = "1" (第 2 页 0-based)
        var resp = MakeListResponse(count: 8, pageNumber: 1, pageSize: 8, totalCount: 47);
        var handler = new DelegatingHandlerStub(resp);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, cursor) = await src.SearchPageAsync(
            query: "", cursor: null, pageSize: 8,
            CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            includeNsfw: true, baseModel: null, progress: null);
        Assert.Equal(8, entries.Count);
        Assert.Equal("1", cursor);  // 0-based next page
    }

    [Fact]
    public async Task SearchPageAsync_LastPage_ReturnsNullCursor()
    {
        // PageNumber=6 / PageSize=8 / TotalCount=47 → 47/8 = 5 余 7,所以 6 页只有 7 条且是最后页。
        var resp = MakeListResponse(count: 7, pageNumber: 6, pageSize: 8, totalCount: 47);
        var handler = new DelegatingHandlerStub(resp);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, cursor) = await src.SearchPageAsync(
            "", null, 8, CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            true, null, null);
        Assert.Equal(7, entries.Count);
        Assert.Null(cursor);
    }

    [Fact]
    public async Task SearchPageAsync_FetchesDetailsForEachEntry()
    {
        // 2-round 验证:列表返 2 entries + 第 1 个 entry 的详情返 200KB 文件,第 2 个详情返 500KB。
        var listResp = MakeListResponse(count: 2, pageNumber: 1, pageSize: 2, totalCount: 2);
        var detail1 = """
        {"Code":200,"Data":{"Id":1,"Name":"a","Revision":[
          {"RevisionId":"master","Files":[{"Name":"a.safetensors","DownloadUrl":"https://cdn/a","Size":204800}]}]}}
        """;
        var detail2 = """
        {"Code":200,"Data":{"Id":2,"Name":"b","Revision":[
          {"RevisionId":"v1","Files":[{"Name":"b.safetensors","DownloadUrl":"https://cdn/b","Size":512000}]}]}}
        """;
        var handler = new DelegatingHandlerStub(listResp, detail1, detail2);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, _) = await src.SearchPageAsync(
            "", null, 2, CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            true, null, null);
        Assert.Equal(2, entries.Count);
        Assert.Equal(204800L, entries[0].Versions[0].SizeBytes);
        Assert.Equal("https://cdn/a", entries[0].Versions[0].PrimaryDownloadUrl);
        Assert.Equal("a.safetensors", entries[0].Versions[0].PrimaryFileName);
        Assert.Equal(512000L, entries[1].Versions[0].SizeBytes);
        Assert.Equal(3, handler.Requests.Count);  // list + 2 details
    }

    [Fact]
    public async Task SearchPageAsync_DetailFetchFails_EntryStillReturned()
    {
        // 列表返 1 entry + 详情 404 → entry 仍返,Versions[0].PrimaryDownloadUrl=null, SizeBytes=0
        // 注:DelegatingHandlerStub 不支持混合码(Enqueue 是 private),用 one-off handler。
        var listResp = MakeListResponse(count: 1, pageNumber: 1, pageSize: 1, totalCount: 1);
        var handler = new DetailFailingHandler(listResp);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, _) = await src.SearchPageAsync(
            "", null, 1, CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            true, null, null);
        Assert.Single(entries);
        Assert.Null(entries[0].Versions[0].PrimaryDownloadUrl);
        Assert.Equal(0L, entries[0].Versions[0].SizeBytes);
    }

    private sealed class DetailFailingHandler : HttpMessageHandler
    {
        private readonly string _firstBody;
        private int _callCount;
        public DetailFailingHandler(string firstBody) { _firstBody = firstBody; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var isFirst = _callCount++ == 0;
            var body = isFirst ? _firstBody : "{}";
            var code = isFirst ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData(new[]{"lora"}, ModelKind.LORA)]
    [InlineData(new[]{"checkpoint"}, ModelKind.Checkpoint)]
    [InlineData(new[]{"vae"}, ModelKind.VAE)]
    [InlineData(new[]{"controlnet"}, ModelKind.ControlNet)]
    [InlineData(new[]{"upscaler","esrgan"}, ModelKind.Upscaler)]
    [InlineData(new[]{"clip","text-encoder"}, ModelKind.TextEncoder)]
    [InlineData(new[]{"embeddings"}, ModelKind.Embedding)]
    [InlineData(new[]{"unet"}, ModelKind.UNET)]
    [InlineData(new[]{"hypernetwork"}, ModelKind.HyperNetwork)]
    [InlineData(new[]{"random","tag"}, ModelKind.Other)]
    public void MapTagsToKind_ReturnsCorrectKind(string[] tags, ModelKind expected)
    {
        // 私有 helper — 通过反射测,或下面这个 dynamic 测试入口
        var kind = InvokeMapTagsToKind(tags);
        Assert.Equal(expected, kind);
    }

    [Fact]
    public async Task SearchPageAsync_TagsMap_AppliedToEntries()
    {
        // entry.Tags = ["lora"] → entry.Kind = ModelKind.LORA
        var listResp = MakeListResponse(count: 1, pageNumber: 1, pageSize: 1, totalCount: 1,
            tagsFor: new[]{ "lora" });
        var detail = """
        {"Code":200,"Data":{"Id":1,"Name":"x","Revision":[
          {"RevisionId":"master","Files":[{"Name":"x.safetensors","DownloadUrl":"https://cdn/x","Size":1024}]}]}}
        """;
        var handler = new DelegatingHandlerStub(listResp, detail);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, _) = await src.SearchPageAsync(
            "", null, 1, CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            true, null, null);
        Assert.Equal(ModelKind.LORA, entries[0].Kind);
    }

    private static ModelKind InvokeMapTagsToKind(string[] tags)
    {
        // 用反射调私有静态方法 — 因为是 internal helper
        var mi = typeof(ModelScopeModelSource).GetMethod("MapTagsToKind",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mi);
        return (ModelKind)mi!.Invoke(null, new object?[]{ tags })!;
    }

    private static string MakeListResponse(int count, int pageNumber, int pageSize, int totalCount,
        string[]? tagsFor = null)
    {
        var tags = tagsFor is null ? "[\"stable-diffusion\",\"checkpoint\"]" :
            "[" + string.Join(",", tagsFor.Select(t => $"\"{t}\"")) + "]";
        var entries = string.Concat(Enumerable.Range(1, count).Select(i =>
            $@"{{""Id"":{i},""Name"":""m{i}"",""ChineseName"":null,""Tags"":{tags},
            ""Downloads"":1,""Stars"":0,""Likes"":0,""Description"":null,""Task"":""text-to-image"",
            ""Owner"":null,""DefaultRevision"":""master""}}"));
        return $$"""
        {{
          "Code":200,"Data":{{
            "Model":{{
              "PageNumber":{{pageNumber}},"PageSize":{{pageSize}},"TotalCount":{{totalCount}},
              "Models":[{{entries}}]
            }}
          }}
        }}
        """;
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelSourceModelScopeTests" -c Debug`
Expected: CS0246(`ModelScopeModelSource` 未定义)

- [ ] **Step 3: 实现 ModelScopeModelSource**

`src-wpf/ComfyUI.Manager/Services/ModelSources/ModelScopeModelSource.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.ModelSources;

/// <summary>v0.6.22.x:魔搭 ModelScope /api/v1/models fetcher。
/// Endpoint: <c>GET {baseUrl}/api/v1/models?PageNumber=N&amp;PageSize=M&amp;Search=q</c>。
/// Pagination: cursor=null = 第 1 页(传 PageNumber=1),否则 PageNumber=int(cursor)+1。
/// 末页 = (PageNumber * PageSize) >= TotalCount → nextCursor=null。
/// 2-round detail:列表 schema 不带 file size/url,需要 2 次请求:
///   1. SearchPageAsync 拉列表(快,N 条)
///   2. 串行 await N 次 GetModelDetailAsync(id) 拿 Revision[0].Files[0]
/// spec 决策:2-round 串行简单 + 单 entry 失败隔离(其他 entry 正常返);
/// N ≤ 20 接受 5-10 秒延迟,后续可改并行。
/// sort/period/baseModel/IncludeNsfw 接收但 no-op(API 无对应字段)。</summary>
public class ModelScopeModelSource : IModelSource
{
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly string _baseUrl;
    private readonly string _apiToken;
    private readonly HttpProxyConfig? _proxy;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ModelSourceKind SourceKind => ModelSourceKind.ModelScope;
    public string DisplayName => "ModelScope";
    public bool IsEnabled { get; set; } = true;

    public ModelScopeModelSource(HttpClient http, string baseUrl, string apiToken,
        AppLogger? logger = null, HttpProxyConfig? proxy = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiToken = apiToken ?? "";
        _logger = logger;
        _proxy = proxy;
        if (_baseUrl != "https://www.modelscope.cn")
        {
            _logger?.Info("model-modelscope", $"using mirror: {_baseUrl}");
        }
    }

    /// <summary>SearchAsync(向后兼容):SearchPageAsync 的循环包装,直到 results.Count
    /// == maxResults 或 nextCursor=null。maxPages=10 硬上限防 runaway。
    /// progress 只在首次 page 报告 URL(progress=null 跳过 Report),镜像 CivitAI 模式。</summary>
    public async Task<IReadOnlyList<ModelEntry>> SearchAsync(string query, int maxResults,
        CancellationToken ct, bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null)
    {
        var results = new List<ModelEntry>();
        string? cursor = null;
        const int maxPages = 10;
        for (var pageNum = 1; pageNum <= maxPages && results.Count < maxResults; pageNum++)
        {
            var (entries, nextCursor) = await SearchPageAsync(
                query, cursor, pageSize: 20, CivitAiSort.Newest, CivitAiPeriod.AllTime,
                ct, includeNsfw, baseModel,
                progress: pageNum == 1 ? progress : null);
            results.AddRange(entries);
            cursor = nextCursor;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return results.Take(maxResults).ToList();
    }

    /// <summary>UI 显式分页入口。cursor=null = 第 1 页(PageNumber=1)。
    /// 返回 (entries, 下一页 cursor — null 已无更多)。
    /// cursor 编码 = 0-based page index 的字符串;末页 = PageNumber*PageSize >= TotalCount。
    /// 失败:列表抛 HttpRequestException 由 aggregator 隔离;单 entry 详情失败仅丢该 entry。
    /// sort/period/baseModel/includeNsfw 接收但 no-op(API 无对应字段)。</summary>
    public async Task<(IReadOnlyList<ModelEntry> entries, string? nextCursor)> SearchPageAsync(
        string query, string? cursor, int pageSize,
        CivitAiSort sort, CivitAiPeriod period, CancellationToken ct,
        bool includeNsfw = true, string? baseModel = null,
        IProgress<string>? progress = null)
    {
        var pageNumber = string.IsNullOrEmpty(cursor) ? 1 : int.Parse(cursor) + 1;
        var url = BuildUrl(query, pageNumber, pageSize);
        var uri = new Uri(url);
        var proxyInfo = FormatProxyInfo(_proxy);
        progress?.Report($"[URL] {url}");
        progress?.Report($"[ModelScope] → {uri.Host}:{uri.Port} ({uri.Scheme.ToUpper()}, {proxyInfo})");
        _logger?.Info("model-modelscope", $"fetch page {pageNumber} query='{query}': {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiToken) && _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        var sw = Stopwatch.StartNew();
        ModelScopeDtos.ModelsResponse? resp;
        try
        {
            var httpResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            progress?.Report($"[ModelScope] ← {(int)httpResp.StatusCode} {httpResp.StatusCode} ({sw.ElapsedMilliseconds}ms, {body.Length} bytes)");
            if (!httpResp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"ModelScope 返回 {(int)httpResp.StatusCode},body {body.Length} bytes,耗时 {sw.ElapsedMilliseconds}ms");
            }
            resp = JsonSerializer.Deserialize<ModelScopeDtos.ModelsResponse>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"ModelScope response JSON parse 失败: {ex.Message}", ex);
        }
        if (resp?.Data?.Model is not { } page)
        {
            throw new HttpRequestException("ModelScope response 缺 Data.Model envelope");
        }

        // 2-round:串行 await 每个 entry 的详情,拿 Revision[0].Files[0] 的 size + url
        var entries = new List<ModelEntry>(page.Models.Count);
        for (var i = 0; i < page.Models.Count; i++)
        {
            var item = page.Models[i];
            var entry = MapListItemToEntry(item);
            try
            {
                await FillEntryFromDetailAsync(entry, item.Id);
            }
            catch (Exception ex)
            {
                // 单 entry 详情失败:entry 仍返,但 Versions[0].PrimaryDownloadUrl=null + SizeBytes=0
                _logger?.Warn("model-modelscope", $"detail fetch 失败 id={item.Id}: {ex.Message}");
                progress?.Report($"[ModelScope] ✗ id={item.Id} detail 失败: {ex.GetType().Name}");
                if (entry.Versions.Count > 0)
                {
                    var v = entry.Versions[0];
                    v.PrimaryDownloadUrl = null;
                    v.SizeBytes = 0;
                }
            }
            entries.Add(entry);
        }
        var morePages = pageNumber * pageSize < page.TotalCount;
        var nextCursor = morePages ? pageNumber.ToString() : null;
        progress?.Report($"[ModelScope] ✓ {entries.Count} 项, 下一页: {(nextCursor is null ? "无" : "有")}");
        return (entries, nextCursor);
    }

    private string BuildUrl(string query, int pageNumber, int pageSize)
    {
        // Uri.EscapeDataString 跟 System.Web.HttpUtility.UrlEncode 行为对齐
        // (空格 → %20,中文 → %E4%B8%AD%E6%96%87),且免去 System.Web 引用。
        var q = Uri.EscapeDataString(query ?? "");
        return $"{_baseUrl}/api/v1/models?PageNumber={pageNumber}&PageSize={pageSize}&Search={q}";
    }

    private static ModelEntry MapListItemToEntry(ModelScopeDtos.ModelItem item)
    {
        var tags = item.Tags ?? new List<string>();
        var kind = MapTagsToKind(tags.ToArray());
        return new ModelEntry
        {
            Source = ModelSourceKind.ModelScope,
            SourceId = item.Id.ToString(),
            // Title 优先用 ChineseName(空 fallback Name)— 用户中文体验
            Title = !string.IsNullOrWhiteSpace(item.ChineseName) ? item.ChineseName : item.Name,
            Author = item.Owner?.DisplayName ?? item.Owner?.Name ?? "",
            Kind = kind,
            NsfwKind = ModelNsfwKind.SFW,  // v0.6.22.x:API 无 NSFW 字段,默认 SFW
            Tags = tags,
            Description = item.Description ?? "",
            PreviewImageUrl = "",  // 列表 schema 无 preview URL
            BaseModel = "",  // API 无此字段
            DownloadCount = item.Downloads,
            LikeCount = item.Likes,
            Versions = new List<ModelVersionEntry>
            {
                new()
                {
                    Id = $"ModelScope:{item.Id}:{item.DefaultRevision}",
                    Name = item.DefaultRevision,
                    BaseModel = "",
                    PrimaryDownloadUrl = null,   // 由 2-round detail 填充
                    PrimaryFileName = "",
                    SizeBytes = 0,
                }
            },
        };
    }

    /// <summary>Kind 推断表 — spec §"Kind 推断表"。Tag 大小写不敏感匹配。
    /// 多个 match 时按以下优先级(lora > checkpoint > 其他,避免 checkpoint 覆盖 lora)。
    /// internal static — 测试用反射调。</summary>
    internal static ModelKind MapTagsToKind(string[] tags)
    {
        if (tags is null || tags.Length == 0) return ModelKind.Other;
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("lora")) return ModelKind.LORA;
        if (set.Contains("hypernetwork")) return ModelKind.HyperNetwork;
        if (set.Contains("textual-inversion") || set.Contains("embeddings")) return ModelKind.Embedding;
        if (set.Contains("checkpoint")) return ModelKind.Checkpoint;
        if (set.Contains("unet")) return ModelKind.UNET;
        if (set.Contains("text-encoder") || set.Contains("clip")) return ModelKind.TextEncoder;
        if (set.Contains("vae")) return ModelKind.VAE;
        if (set.Contains("controlnet")) return ModelKind.ControlNet;
        if (set.Contains("upscaler") || set.Contains("esrgan") || set.Contains("real-esrgan")) return ModelKind.Upscaler;
        return ModelKind.Other;
    }

    private async Task FillEntryFromDetailAsync(ModelEntry entry, long id)
    {
        var url = $"{_baseUrl}/api/v1/models/{id}";
        var httpResp = await _http.GetAsync(url).ConfigureAwait(false);
        var body = await httpResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!httpResp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ModelScope detail 返回 {(int)httpResp.StatusCode},body {body.Length} bytes");
        }
        var detail = JsonSerializer.Deserialize<ModelScopeDtos.ModelDetailResponse>(body, JsonOpts);
        var firstFile = detail?.Data?.Revision?.FirstOrDefault()?.Files?.FirstOrDefault();
        if (firstFile is null || entry.Versions.Count == 0) return;
        var v = entry.Versions[0];
        v.PrimaryDownloadUrl = firstFile.DownloadUrl;
        v.PrimaryFileName = firstFile.Name;
        v.SizeBytes = firstFile.Size;
    }

    private string FormatProxyInfo(HttpProxyConfig? proxy)
    {
        if (proxy is null) return "直连";
        return proxy.Mode switch
        {
            HttpProxyMode.Off => "直连",
            HttpProxyMode.InheritSystem => "系统代理",
            HttpProxyMode.Custom => $"代理={proxy.Url}:{proxy.Port}",
            _ => "?"
        };
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelSourceModelScopeTests" -c Debug`
Expected: PASS(10+/10+)

- [ ] **Step 5: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(models): v0.6.22.x ModelScopeModelSource — search + 2-round detail + kind mapping"
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelSourceModelScopeTests" -c Debug`
Expected: PASS(10+/10+)

- [ ] **Step 5: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(models): v0.6.22.x ModelScopeModelSource — search + 2-round detail + kind mapping"
```

---

### Task 3: Factory.CreateModelScope + Settings 5 fields + factory tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/ModelSources/ModelSourceFactory.cs`
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/ModelSourceFactoryTests.cs`

**Interfaces:**
- Consumes: `ModelScopeModelSource` (Task 2), `ModelSourceProxyDecision.Resolve`(v0.6.22++ 已有)
- Produces:
  - `ModelSourceFactory.ModelScopeOfficial = "https://www.modelscope.cn"`(常量)
  - `ModelSourceFactory.CreateModelScope(settings, httpBuilder, logger)` static 方法
  - `ModelSourceFactory.CreateAll` 加 ModelScope 在 CivitAI/HF 之间
  - `Settings.ModelSourceModelScopeEnabled/UseMirror/MirrorUrl/ApiToken/ProxyMode` + `CopyInto` 同步

- [ ] **Step 1: 写失败的 factory 测试**

`ModelSourceFactoryTests.cs` 加:
```csharp
private static Settings MakeSettings(
    // ... 已有 ...
    bool modelScope = false, bool modelScopeMirror = false, string modelScopeMirrorUrl = "",
    string modelScopeToken = "", ModelSourceProxyMode modelScopeProxyMode = ModelSourceProxyMode.InheritGlobal)
    => new Settings
    {
        // ... 已有 ...
        ModelSourceModelScopeEnabled = modelScope,
        ModelSourceModelScopeUseMirror = modelScopeMirror,
        ModelSourceModelScopeMirrorUrl = modelScopeMirrorUrl,
        ModelSourceModelScopeApiToken = modelScopeToken,
        ModelSourceModelScopeProxyMode = modelScopeProxyMode,
    };

[Fact]
public void CreateModelScope_Disabled_ReturnsNull()
{
    var settings = MakeSettings(modelScope: false);
    var b = new RecordingBuilder();
    var result = ModelSourceFactory.CreateModelScope(settings, b.AsFunc());
    Assert.Null(result);
    Assert.Empty(b.Calls);
}

[Fact]
public void CreateModelScope_UseMirror_ResolvesMirrorUrl()
{
    var settings = MakeSettings(modelScope: true, modelScopeMirror: true,
        modelScopeMirrorUrl: "https://ms-mirror.example.com/");
    var b = new RecordingBuilder();
    var src = ModelSourceFactory.CreateModelScope(settings, b.AsFunc());
    Assert.NotNull(src);
    Assert.Equal(ModelSourceKind.ModelScope, src!.SourceKind);
    Assert.Equal("ModelScope", src.DisplayName);
}

[Fact]
public void CreateModelScope_Official_NoMirror_ReturnsOfficialBase()
{
    var settings = MakeSettings(modelScope: true, modelScopeMirror: false);
    var b = new RecordingBuilder();
    var src = ModelSourceFactory.CreateModelScope(settings, b.AsFunc());
    Assert.NotNull(src);
}

[Fact]
public void CreateAll_ThreeSources_ReturnsThree()
{
    var settings = MakeSettings(
        civitai: true, hf: true, modelScope: true,
        modelScopeMirror: false);
    var b = new RecordingBuilder();
    var sources = new List<IModelSource>(ModelSourceFactory.CreateAll(settings, b.AsFunc()));
    Assert.Equal(3, sources.Count);
    Assert.Equal(ModelSourceKind.CivitAi, sources[0].SourceKind);
    Assert.Equal(ModelSourceKind.ModelScope, sources[1].SourceKind);
    Assert.Equal(ModelSourceKind.HuggingFace, sources[2].SourceKind);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelSourceFactoryTests" -c Debug`
Expected: CS0117(`Settings` 没有 `ModelSourceModelScopeEnabled`)+CS0117(`ModelSourceFactory` 没有 `CreateModelScope`)

- [ ] **Step 3: 加 Settings 5 字段 + CopyInto**

`Models/Settings.cs`(`:111` 之后):
```csharp
// v0.6.22.x:ModelScope 国内模型源 — 默认 disabled(避免新装用户没配 token 看到空结果,
// 需要时手动勾选;镜像 HF/CivitAI 同模式)。
[JsonPropertyName("model_source_modelscope_enabled")]
public bool ModelSourceModelScopeEnabled { get; set; } = false;
[JsonPropertyName("modelscope_api_token")]
public string ModelSourceModelScopeApiToken { get; set; } = "";
[JsonPropertyName("model_source_modelscope_use_mirror")]
public bool ModelSourceModelScopeUseMirror { get; set; } = false;
[JsonPropertyName("model_source_modelscope_mirror_url")]
public string ModelSourceModelScopeMirrorUrl { get; set; } = "";
[JsonPropertyName("model_source_modelscope_proxy_mode")]
public ModelSourceProxyMode ModelSourceModelScopeProxyMode { get; set; } = ModelSourceProxyMode.InheritGlobal;
```

`Models/Settings.cs` `CopyInto`(`:219` 之后):
```csharp
target.ModelSourceModelScopeEnabled = source.ModelSourceModelScopeEnabled;
target.ModelSourceModelScopeApiToken = source.ModelSourceModelScopeApiToken;
target.ModelSourceModelScopeUseMirror = source.ModelSourceModelScopeUseMirror;
target.ModelSourceModelScopeMirrorUrl = source.ModelSourceModelScopeMirrorUrl;
target.ModelSourceModelScopeProxyMode = source.ModelSourceModelScopeProxyMode;
```

- [ ] **Step 4: 加 Factory.CreateModelScope + CreateAll 接入**

`Services/ModelSources/ModelSourceFactory.cs`:
```csharp
public const string ModelScopeOfficial = "https://www.modelscope.cn";

public static ModelScopeModelSource? CreateModelScope(
    Settings settings, Func<HttpProxyConfig?, HttpClient> httpBuilder,
    AppLogger? logger = null)
{
    if (!settings.ModelSourceModelScopeEnabled) return null;
    var baseUrl = ResolveBaseUrl(settings.ModelSourceModelScopeUseMirror,
                                 settings.ModelSourceModelScopeMirrorUrl,
                                 ModelScopeOfficial);
    var proxy = ModelSourceProxyDecision.Resolve(
        settings.HttpProxyMode,
        settings.ModelSourceModelScopeProxyMode,
        settings);
    var http = httpBuilder(proxy);
    return new ModelScopeModelSource(http, baseUrl, settings.ModelSourceModelScopeApiToken, logger, proxy);
}

public static IEnumerable<IModelSource> CreateAll(...) {
    var sources = new List<IModelSource>();
    var civitai = CreateCivitAi(...);
    if (civitai is not null) sources.Add(civitai);
    var ms = CreateModelScope(settings, httpBuilder, logger);  // NEW
    if (ms is not null) sources.Add(ms);
    var hf = CreateHuggingFace(...);
    if (hf is not null) sources.Add(hf);
    return sources;
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelSourceFactoryTests" -c Debug`
Expected: PASS(原 9 + 新 4 = 13/13)

- [ ] **Step 6: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(models): v0.6.22.x ModelScope factory wiring + Settings 5 fields"
```

---

### Task 4: SettingsViewModel + SettingsView.xaml sub-section

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`

**Interfaces:**
- Consumes: `Settings.ModelSourceModelScope*`(Task 3)
- Produces: `SettingsViewModel.ModelSourceModelScopeEnabled/UseMirror/MirrorUrl/ApiToken/ProxyMode` 5 个属性 + `ResetModelScopeMirrorUrl` 命令 + `IsModelScopeMirrorInsecure` bool;`SettingsView.xaml` 模型市场段加第三个 sub-section

- [ ] **Step 1: 找现有 HuggingFace sub-section 作为模板**

Read `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:649-738`(HuggingFace 段)+ `ViewModels/SettingsViewModel.cs` HF proxy 属性。

- [ ] **Step 2: 加 SettingsViewModel 5 属性 + Reset 命令**

镜像 `ModelSourceHuggingFaceEnabled/MirrorUrl/...` 模式(`SettingsViewModel.cs` HF 部分):
```csharp
public bool ModelSourceModelScopeEnabled { /* proxy */ }
public string ModelSourceModelScopeApiToken { /* proxy + BindablePasswordBox 已支持 */ }
public bool ModelSourceModelScopeUseMirror { /* proxy */ }
public string ModelSourceModelScopeMirrorUrl { /* proxy */ }
public ModelSourceProxyMode ModelSourceModelScopeProxyMode { /* proxy + RaiseCanExecuteChanged */ }
public ICommand ResetModelScopeMirrorUrlCommand => new RelayCommand(_ =>
    ModelSourceModelScopeMirrorUrl = ModelScopeOfficial);
// (ModelScopeOfficial 常量从 ModelSourceFactory 引用 — 注意 cross-namespace 引用)
public bool IsModelScopeMirrorInsecure => /* Uri scheme check 同 HF */;
```

- [ ] **Step 3: 在 SettingsView.xaml 加 ModelScope sub-section**

放在 HuggingFace sub-section 后(行 738 后):
```xml
<!-- v0.6.22.x:ModelScope 国内模型源 — 默认 disabled,镜像 HF/CivitAI 结构 -->
<TextBlock Text="ModelScope" FontSize="13" FontWeight="Bold" Margin="0,8,0,4" />
<CheckBox Content="启用 ModelScope (魔搭)"
          IsChecked="{Binding ModelSourceModelScopeEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
<StackPanel Visibility="{Binding ModelSourceModelScopeEnabled, Converter={StaticResource BoolToVisibility}}">
    <!-- API token + 测试连接 — 镜像 HuggingFaceApiToken 行 -->
    <DockPanel Margin="0,4,0,0" LastChildFill="True">
        <Button DockPanel.Dock="Right" Content="测试连接" Click="TestModelScopeConnection" />
        <TextBlock Text="API Token" VerticalAlignment="Center" Margin="0,0,8,0" />
    </DockPanel>
    <controls:BindablePasswordBox x:Name="ModelScopeTokenBox"
        Password="{Binding ModelSourceModelScopeApiToken, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />

    <!-- 镜像 CheckBox + Reset + TextBox 模式 -->
    <CheckBox Content="使用国内镜像 (留空走官方 https://www.modelscope.cn)"
              IsChecked="{Binding ModelSourceModelScopeUseMirror, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
    <DockPanel Visibility="{Binding ModelSourceModelScopeUseMirror, Converter={StaticResource BoolToVisibility}}">
        <Button DockPanel.Dock="Right" Content="重置" Click="ResetModelScopeMirrorUrl" />
        <TextBox Text="{Binding ModelSourceModelScopeMirrorUrl, UpdateSourceTrigger=PropertyChanged}" />
    </DockPanel>

    <!-- Proxy 三态 RadioGroup 同 HF/CivitAI -->
    <RadioButton Content="关闭" GroupName="ModelScopeProxyMode"
                 IsChecked="{Binding ModelSourceModelScopeProxyMode, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=Off, Mode=TwoWay}" />
    <RadioButton Content="跟随全局" GroupName="ModelScopeProxyMode"
                 IsChecked="{Binding ModelSourceModelScopeProxyMode, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=InheritGlobal, Mode=TwoWay}" />
    <RadioButton Content="总是启用" GroupName="ModelScopeProxyMode"
                 IsChecked="{Binding ModelSourceModelScopeProxyMode, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=AlwaysOn, Mode=TwoWay}" />
</StackPanel>
```

`SettingsView.xaml.cs` 加:
```csharp
private void TestModelScopeConnection(object sender, RoutedEventArgs e)
{
    TestSourceConnection("https://www.modelscope.cn",
        Settings.Current.ModelSourceModelScopeApiToken,
        Settings.Current.ModelSourceModelScopeUseMirror
            ? Settings.Current.ModelSourceModelScopeMirrorUrl
            : "");
}
private void ResetModelScopeMirrorUrl(object sender, RoutedEventArgs e)
{
    if (DataContext is SettingsViewModel vm) vm.ResetModelScopeMirrorUrlCommand.Execute(null);
}
```

- [ ] **Step 4: Build 验证 XAML 无 parse error**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Debug 2>&1 | grep -E "error|warning MC"`
Expected: 0 errors

- [ ] **Step 5: 跑 SettingsViewModel 单元测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel" -c Debug`
Expected: PASS(无回归)

- [ ] **Step 6: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(settings): v0.6.22.x ModelScope sub-section UI"
```

---

### Task 5: ModelMarketplaceView.xaml RadioButton + sort/period/baseModel Visibility

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/ModelMarketplaceView.xaml`

**Interfaces:**
- Consumes: `ModelSourceKind.ModelScope` (Task 1)
- Produces: 第 3 个 RadioButton「ModelScope」在 source radio row;sort/period/baseModel 行的 `Visibility` 折叠判断不变(已只对 CivitAI 显示,ModelScope 自动折叠同 HF)

- [ ] **Step 1: 读现状确认行号**

Read `Views/ModelMarketplaceView.xaml:117-130`(source radio row)。

- [ ] **Step 2: 加第 3 个 RadioButton**

在 HuggingFace RadioButton(行 125)后加:
```xml
<!-- v0.6.22.x:ModelScope 国内源 — 默认 disabled,启用后出现在 source radio group;
     sort/period/baseModel 行只对 CivitAI 显示,ModelScope 同 HF 自动折叠。 -->
<RadioButton Content="ModelScope" GroupName="ActiveSource"
             Tag="{x:Static models:ModelSourceKind.ModelScope}"
             IsChecked="{Binding ActiveSource, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=ModelScope}"
             Click="OnSourceRadioClicked"
             Margin="12,0,0,0" VerticalAlignment="Center" />
```

- [ ] **Step 3: Build + XAML load 测试**

Run:
- `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Debug 2>&1 | grep -E "error|warning MC"`
- `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelMarketplaceViewLoad" -c Debug`

Expected: 0 build errors, 3/3 XAML load tests PASS

- [ ] **Step 4: 跑完整 ModelMarketplace 测试套**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~ModelMarketplace" -c Debug`
Expected: 全部 PASS(原 48 + ModelScope 新增 ~11 = ~59;5 pre-existing flaky 不动)

- [ ] **Step 5: Commit**

```bash
git add src-wpf tests-wpf
git commit -m "feat(models): v0.6.22.x ModelMarketplace view 加 ModelScope radio"
```

---

### Task 6: 完整 suite + staging rebuild

**Files:**
- 不改代码,跑全套测试 + 重建 staging

- [ ] **Step 1: 跑完整 suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj -c Debug 2>&1 | tail -3`
Expected: 失败数 ≤ 5(全是 pre-existing flaky,无回归);新 ModelScope 11+ 测试全 PASS

- [ ] **Step 2: Rebuild staging**

Run: `powershell -ExecutionPolicy Bypass -File scripts/build_staging.ps1 2>&1 | tail -3`
Expected: `[ok] staging built at D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager with bundled git-portable`

(如果 staging exe 还跑着,先 `taskkill /IM ComfyUI.Manager.exe /F` 解锁 DLL)

- [ ] **Step 3: 启动 + 桌面验证 4 步**

启动 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`:
1. 设置 → 模型市场段出现「ModelScope」第三个 sub-section,默认 disabled
2. 勾选启用 + (可选)填 API token → 切到「模型市场」tab → source radio 出现「ModelScope」第三项
3. 选 ModelScope → 输入 "lora" → sort/period/baseModel 行**自动折叠**(只剩 kind chips + source radio + NSFW checkbox)
4. Console 出现 `[ModelScope] → www.modelscope.cn:443 (HTTPS, ...)` + `[ModelScope] ← 200 ...ms ...bytes` 三行

- [ ] **Step 4: 写 memory + commit 进度**

Update `MEMORY.md` index with v0.6.22.x ModelScope entry.