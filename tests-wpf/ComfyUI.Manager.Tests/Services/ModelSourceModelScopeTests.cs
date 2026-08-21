using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceModelScopeTests
{
    private static HttpClient CreateClient(HttpMessageHandler h)
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
        // URL 参数校验见 SearchPageAsync_BuildsUrlWithExpectedQueryParams 测试。
    }

    [Fact]
    public async Task SearchPageAsync_BuildsUrlWithExpectedQueryParams()
    {
        // 验证 BuildUrl 输出含 PageNumber=1, PageSize=8, Search=<encoded query>。
        // Search 编码双兼容(Uri.ToString() 不解码,某些 .NET 版本会解码)—
        // 见 v0.6.22+ URL logged 教训。
        var resp = MakeListResponse(count: 1, pageNumber: 1, pageSize: 8, totalCount: 1);
        var handler = new DelegatingHandlerStub(resp);
        var src = new ModelScopeModelSource(CreateClient(handler), "https://www.modelscope.cn", "");
        var (entries, _) = await src.SearchPageAsync(
            "测试", null, 8, CivitAiSort.Newest, CivitAiPeriod.AllTime, default,
            true, null, null);
        Assert.Single(entries);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("PageNumber=1", url);
        Assert.Contains("PageSize=8", url);
        Assert.True(url.Contains("测试") || url.Contains("%E4%B8%AD%E6%96%87"),
            $"Search query 应在 URL 中(原文或 percent-encoded),实际:{url}");
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
    [InlineData(new[]{"controlnet"}, ModelKind.Controlnet)]
    [InlineData(new[]{"upscaler","esrgan"}, ModelKind.Upscaler)]
    [InlineData(new[]{"clip","text-encoder"}, ModelKind.Other)]
    [InlineData(new[]{"embeddings"}, ModelKind.TextualInversion)]
    [InlineData(new[]{"unet"}, ModelKind.Other)]
    [InlineData(new[]{"hypernetwork"}, ModelKind.Hypernetwork)]
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
            "[" + string.Join(",", tagsFor.Select(t => "\"" + t + "\"")) + "]";
        var entries = string.Join(",", Enumerable.Range(1, count).Select(i =>
            "{\"Id\":" + i + ",\"Name\":\"m" + i + "\",\"ChineseName\":null,\"Tags\":" + tags + ","
            + "\"Downloads\":1,\"Stars\":0,\"Likes\":0,\"Description\":null,\"Task\":\"text-to-image\","
            + "\"Owner\":null,\"DefaultRevision\":\"master\"}"));
        return "{"
            + "\"Code\":200,\"Data\":{"
            + "\"Model\":{"
            + "\"PageNumber\":" + pageNumber + ",\"PageSize\":" + pageSize + ",\"TotalCount\":" + totalCount + ","
            + "\"Models\":[" + entries + "]"
            + "}}"
            + "}";
    }
}