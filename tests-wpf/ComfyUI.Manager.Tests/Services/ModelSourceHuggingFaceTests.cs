using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceHuggingFaceTests
{
    private static (HuggingFaceModelSource src, RecordingHandler handler) MakeSource(string baseUrl = "https://huggingface.co", string token = "")
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new System.Uri(baseUrl) };
        var src = new HuggingFaceModelSource(http, baseUrl, token);
        return (src, handler);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_HitsBaseUrl()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"stabilityai/sdxl\"}]");
        handler.QueueResponse("{\"id\":\"stabilityai/sdxl\",\"sha\":\"abc123\",\"siblings\":[{\"rfilename\":\"sdxl.safetensors\",\"size\":1024}],\"tags\":[\"diffusers\",\"checkpoint\"]}");

        var results = await src.SearchAsync("", 10, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal("stabilityai/sdxl", results[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_WithToken_SendsBearerHeader()
    {
        var (src, handler) = MakeSource(token: "hf_test_token_123");
        handler.QueueResponse("[{\"id\":\"private/repo\"}]");
        handler.QueueResponse("{\"id\":\"private/repo\",\"sha\":\"xyz\",\"siblings\":[{\"rfilename\":\"m.safetensors\",\"size\":512}],\"tags\":[]}");

        await src.SearchAsync("test", 1, CancellationToken.None);
        Assert.NotEmpty(handler.Requests);
        Assert.Contains(handler.Requests, r => r.Headers.Authorization?.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "hf_test_token_123");
    }

    [Fact]
    public void MapKindFromTags_TagsContainsLora_MapsToLoraKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "lora", "image" });
        Assert.Equal(ModelKind.LORA, kind);
    }

    [Fact]
    public void MapKindFromTags_TagsContainsCheckpoint_MapsToCheckpointKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "checkpoint", "text-to-image" });
        Assert.Equal(ModelKind.Checkpoint, kind);
    }

    [Fact]
    public void MapKindFromTags_UnknownKindTags_MapsToOther()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "diffusers", "image" });
        Assert.Equal(ModelKind.Other, kind);
    }

    [Fact]
    public void MapKindFromTags_TagsContainsVae_MapsToVaeKind()
    {
        var kind = HuggingFaceModelSource.MapKindFromTags(new List<string> { "vae", "diffusers" });
        Assert.Equal(ModelKind.VAE, kind);
    }

    [Fact]
    public async Task MapToModelEntry_TagContainsNsfw_SetsNsfwRating()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"nsfw/model\"}]");
        handler.QueueResponse("{\"id\":\"nsfw/model\",\"sha\":\"abc\",\"siblings\":[{\"rfilename\":\"m.safetensors\",\"size\":1024}],\"tags\":[\"lora\",\"nsfw\"]}");

        var results = await src.SearchAsync("", 1, CancellationToken.None);
        Assert.Equal(ModelNsfwKind.NSFW, results[0].NsfwKind);
    }

    [Fact]
    public async Task MapToModelEntry_SiblingsList_PicksLargestSafetensors()
    {
        var (src, handler) = MakeSource();
        handler.QueueResponse("[{\"id\":\"multi/file\"}]");
        handler.QueueResponse("{\"id\":\"multi/file\",\"sha\":\"abc\",\"siblings\":[{\"rfilename\":\"small.safetensors\",\"size\":1024},{\"rfilename\":\"large.safetensors\",\"size\":9999999}],\"tags\":[\"checkpoint\"]}");

        var results = await src.SearchAsync("", 1, CancellationToken.None);
        Assert.Equal("large.safetensors", results[0].Versions[0].Files[0].Name);
        Assert.Equal(9999999, results[0].Versions[0].SizeBytes);
    }

    [Fact(Skip = "Real HF API fetch — not run in CI")]
    public async Task SearchAsync_RealHF_ReturnsAtLeastOneResult()
    {
        var (src, handler) = MakeSource("https://huggingface.co");
        var results = await src.SearchAsync("stable-diffusion", 5, CancellationToken.None);
        Assert.NotEmpty(results);
    }

    private class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        private readonly Queue<string> _responseBodies = new();
        public void QueueResponse(string body) => _responseBodies.Enqueue(body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = _responseBodies.Count > 0 ? _responseBodies.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
