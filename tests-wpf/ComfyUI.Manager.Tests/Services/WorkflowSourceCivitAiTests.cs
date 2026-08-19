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
    public async Task SearchAsync_ModelWithJsonFile_ReturnsEntry()
    {
        // v0.6.22: model-source endpoint — 1 model + 1 version + 2 files (.json + .safetensors)
        // → 1 WorkflowEntry with WorkflowJsonUrl from the .json file
        var json = """
{"items":[{"id":123,"name":"Workflow A","creator":{"username":"bob"},"tags":["controlnet"],"modelVersions":[{"id":1,"files":[{"name":"workflow.json","downloadUrl":"https://files/wf.json"},{"name":"model.safetensors","downloadUrl":"https://files/m.safetensors"}],"images":[{"url":"https://img/preview.jpg"}]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal(WorkflowSourceKind.CivitAi, result[0].Source);
        Assert.Equal("123", result[0].SourceId);
        Assert.Equal("Workflow A", result[0].Title);
        Assert.Equal("bob", result[0].Author);
        Assert.Equal("https://files/wf.json", result[0].WorkflowJsonUrl);
        Assert.Equal("https://img/preview.jpg", result[0].PreviewImageUrl);
        Assert.Equal("https://civitai.com/models/123", result[0].SourceUrl);
        Assert.Equal(new[] { "controlnet" }, result[0].Tags.ToArray());
    }

    [Fact]
    public async Task SearchAsync_NoJsonFile_SkipsEntry()
    {
        // v0.6.22: entry with only .safetensors file → empty list (matches v0.6.19 R1
        // "skip on missing" semantic — model-source uses json-file presence as the
        // signal, not meta.workflow.workflowJson)
        var json = """
{"items":[{"id":1,"name":"Safetensors only","creator":{"username":"x"},"modelVersions":[{"id":1,"files":[{"name":"model.safetensors","downloadUrl":"https://files/m.safetensors"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MultipleVersions_PicksFirstVersionJsonFile()
    {
        // v0.6.22: model with 2 versions, each with .json → uses first version's .json
        var json = """
{"items":[{"id":99,"name":"Multi version","creator":{"username":"alice"},"tags":[],"modelVersions":[{"id":1,"files":[{"name":"v1.json","downloadUrl":"https://files/v1.json"}],"images":[{"url":"https://img/v1.jpg"}]},{"id":2,"files":[{"name":"v2.json","downloadUrl":"https://files/v2.json"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("https://files/v1.json", result[0].WorkflowJsonUrl);
        Assert.Equal("https://img/v1.jpg", result[0].PreviewImageUrl);
    }

    [Fact]
    public async Task SearchAsync_EmptyCreatorUsername_DoesNotThrow()
    {
        // v0.6.22: model with empty creator.username → WorkflowEntry.Author = ""
        // (no NullReferenceException, no exception bubbles out)
        var json = """
{"items":[{"id":42,"name":"Anon","creator":{"username":""},"modelVersions":[{"id":1,"files":[{"name":"wf.json","downloadUrl":"https://files/wf.json"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("", result[0].Author);
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

    [Fact]
    public async Task SearchAsync_QueryFilterByTag_MatchesEntry()
    {
        // v0.6.22: query match against tag array (new capability vs v0.6.19
        // title/author-only filter — model-source exposes tags[] per entry)
        var json = """
{"items":[{"id":1,"name":"Apple pie","creator":{"username":"u"},"tags":["lora"],"modelVersions":[{"id":1,"files":[{"name":"wf.json","downloadUrl":"https://x/1.json"}],"images":[]}]},{"id":2,"name":"Banana split","creator":{"username":"v"},"tags":["controlnet"],"modelVersions":[{"id":2,"files":[{"name":"wf.json","downloadUrl":"https://x/2.json"}],"images":[]}]}]}
""";
        var src = new CivitAiSource(MockHttp(json));

        var result = await src.SearchAsync(query: "control", maxResults: 10);

        Assert.Single(result);
        Assert.Equal("2", result[0].SourceId);
    }

    [Fact(Skip = "Integration: hits real CivitAI /api/v1/models?types=WORKFLOW")]
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