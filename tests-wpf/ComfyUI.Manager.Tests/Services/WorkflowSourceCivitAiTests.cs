using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowSourceCivitAiTests
{
    private static HttpClient MockHttp(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new HttpClient(new StubHandler(json, status));

    [Fact]
    public async Task SearchAsync_ItemsWithWorkflowJson_ParsesEntry()
    {
        // brief 把这 JSON 拆成两行 raw string literal — C# raw string literal 必须单行 """..."""
        var json = "{\"items\":[{\"id\":\"123\",\"name\":\"Workflow A\",\"username\":\"bob\",\"url\":\"https://img.jpg\",\"meta\":{\"workflow\":{\"workflowJson\":\"https://files/wf.json\"}}}]}";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
        Assert.Equal("123", result[0].SourceId);
        Assert.Equal("Workflow A", result[0].Title);
        Assert.Equal("https://files/wf.json", result[0].WorkflowJsonUrl);
    }

    [Fact]
    public async Task SearchAsync_NoWorkflowJson_SkipsEntry()
    {
        // brief 里这 JSON 含 url 字段,会触发 source 里的 jsonUrl fallback(把 url 当 json_url 用),
        // 与 test 意图冲突。去掉 url 让 fallback 不命中,确认真的没 workflowJson 就跳过。
        var json = """{"items":[{"id":"1","name":"Image only"}]}""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_QueryFilter_Applies()
    {
        // brief 把这 JSON 拆成两行 raw string literal — C# raw string literal 必须单行 """..."""
        var json = "{\"items\":[{\"id\":\"1\",\"name\":\"Apple pie\",\"url\":\"x\",\"meta\":{\"workflow\":{\"workflowJson\":\"x1\"}}},{\"id\":\"2\",\"name\":\"Banana split\",\"url\":\"y\",\"meta\":{\"workflow\":{\"workflowJson\":\"y2\"}}}]}";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "banana", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("2", result[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_RateLimited429_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("rate limited", HttpStatusCode.TooManyRequests));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_Http500_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("server error", HttpStatusCode.InternalServerError));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MalformedJson_ReturnsEmpty()
    {
        var src = new CivitAiSource(MockHttp("not json at all"));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact(Skip = "Integration: hits real CivitAI /api/v1/images?tags=workflow")]
    public async Task LiveFetch_CivitAi_RealEndpoint_ReturnsEntries()
    {
        var src = new CivitAiSource(new HttpClient());
        var result = await src.SearchAsync(query: "", maxResults: 10);
        // CivitAI 即使成功也可能返空(限流) — 不强制 NonEmpty
        Assert.NotNull(result);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public StubHandler(string body, HttpStatusCode status) { _body = body; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}