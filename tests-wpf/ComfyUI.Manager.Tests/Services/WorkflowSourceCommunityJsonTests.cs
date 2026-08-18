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

public class WorkflowSourceCommunityJsonTests
{
    private static HttpClient MockHttp(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new DelegatingHandlerStub(responseJson, status);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task SearchAsync_ItemsShape_ParsesEntries()
    {
        var json = """{"items":[{"id":"abc","title":"Portrait Gen","author":"alice","json_url":"https://x.com/w.json","tags":["portrait","anime"]}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("abc", result[0].SourceId);
        Assert.Equal(WorkflowSourceKind.CommunityJson, result[0].Source);
        Assert.Equal("Portrait Gen", result[0].Title);
        Assert.Equal("alice", result[0].Author);
        Assert.Equal(2, result[0].Tags.Count);
        Assert.Contains("portrait", result[0].Tags);
    }

    [Fact]
    public async Task SearchAsync_ArrayShape_ParsesEntries()
    {
        var json = """[{"id":"x","title":"X","json_url":"https://x"}]""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("x", result[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_QueryText_FiltersByTitleOrAuthor()
    {
        // brief 把这 JSON 拆成两行 raw string —— C# 11 raw string literal 不支持,
        // 必须是单行 """..."""
        var json = "{\"items\":[{\"id\":\"a\",\"title\":\"Apple\",\"author\":\"alice\",\"json_url\":\"https://a\"},{\"id\":\"b\",\"title\":\"Banana\",\"author\":\"bob\",\"json_url\":\"https://b\"}]}";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "ban", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("b", result[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_MissingJsonUrl_SkipsEntry()
    {
        var json = """{"items":[{"id":"a","title":"No URL"}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_HttpFailure_ReturnsEmpty()
    {
        var src = new CommunityJsonSource(MockHttp("server error", HttpStatusCode.InternalServerError));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MaxResults_LimitsCount()
    {
        var items = string.Join(",", Enumerable.Range(0, 50)
            .Select(i => $$"""{"id":"{{i}}","title":"t{{i}}","json_url":"https://{{i}}"}"""));
        var json = $$"""{"items":[{{items}}]}""";
        var src = new CommunityJsonSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 5);

        Assert.Equal(5, result.Count);
    }

    [Fact(Skip = "Integration: hits real CommunityJson endpoint")]
    public async Task LiveFetch_CommunityJson_RealEndpoint_ReturnsEntries()
    {
        var src = new CommunityJsonSource(new HttpClient());
        var result = await src.SearchAsync(query: "", maxResults: 10);
        Assert.NotEmpty(result);
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public DelegatingHandlerStub(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body; _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}